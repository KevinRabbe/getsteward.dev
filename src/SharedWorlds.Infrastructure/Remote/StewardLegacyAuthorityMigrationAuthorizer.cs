using SharedWorlds.Core.Abstractions;
using SharedWorlds.Core.Domain;
using SharedWorlds.Infrastructure.Sessions;

namespace SharedWorlds.Infrastructure.Remote;

/// <summary>
/// Temporary cutover bridge for older Shared Worlds. It never grants peer authority from legacy
/// membership alone. Instead it proves a durable Backend.Api retirement tombstone for the exact
/// canonical head, acquiring the old writable reservation only when retirement has not happened yet.
/// A completed tombstone is restart-resumable and no legacy reservation is reacquired afterward.
/// </summary>
public sealed class StewardLegacyAuthorityMigrationAuthorizer :
    IPeerWorldAuthorityMigrationAuthorizer
{
    private readonly IWorldSessionCoordinator _legacyCoordinator;
    private readonly StewardWritableReservationRegistry _reservations;
    private readonly StewardLegacyAuthorityRetirementClient _retirement;
    private readonly UserIdentity _authenticatedUser;

    public StewardLegacyAuthorityMigrationAuthorizer(
        IWorldSessionCoordinator legacyCoordinator,
        StewardWritableReservationRegistry reservations,
        StewardLegacyAuthorityRetirementClient retirement,
        UserIdentity authenticatedUser)
    {
        ArgumentNullException.ThrowIfNull(legacyCoordinator);
        ArgumentNullException.ThrowIfNull(reservations);
        ArgumentNullException.ThrowIfNull(retirement);
        ArgumentNullException.ThrowIfNull(authenticatedUser);
        _legacyCoordinator = legacyCoordinator;
        _reservations = reservations;
        _retirement = retirement;
        _authenticatedUser = authenticatedUser;
    }

    public async Task<bool> CanInitializePeerAuthorityAsync(
        World world,
        UserIdentity proposedHolder,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(proposedHolder);
        if (!SameUser(proposedHolder, _authenticatedUser))
        {
            return false;
        }

        var retired = await _retirement.GetAsync(world.Id, cancellationToken);
        if (retired is not null)
        {
            ValidateRetiredHead(world, retired);
            ResolveLocalLease(retired);
            return true;
        }

        await _legacyCoordinator.AcquireHostAsync(
            world.Id,
            proposedHolder,
            cancellationToken);
        var lease = _reservations.Get(world.Id)
            ?? throw new InvalidDataException(
                "Legacy coordinator reported writable authority without registering its exact reservation lease.");

        try
        {
            ValidateLeaseHolder(lease, proposedHolder);
            ValidateStartingHead(world, lease.StartingHead);
        }
        catch
        {
            await TryReleaseUnretiredLeaseAsync(world.Id, proposedHolder);
            throw;
        }

        StewardLegacyAuthorityRetirementEvidence retirementEvidence;
        try
        {
            retirementEvidence = await _retirement.RetireAsync(
                world.Id,
                lease.SessionId,
                lease.Generation,
                cancellationToken);
        }
        catch
        {
            // Do not release on an ambiguous retirement failure. The backend may already have made
            // retirement durable. Keeping the local lease lets a retry first resolve the tombstone.
            throw;
        }

        ValidateRetiredHead(world, retirementEvidence);

        var verified = await _retirement.GetAsync(world.Id, cancellationToken)
            ?? throw new IOException(
                "Backend acknowledged legacy authority retirement but read-back did not return the durable tombstone.");
        ValidateSameRetirement(retirementEvidence, verified);
        ValidateRetiredHead(world, verified);
        ResolveLocalLease(verified);
        return true;
    }

    private static void ValidateLeaseHolder(
        StewardWritableReservationLease lease,
        UserIdentity expectedHolder)
    {
        if (!string.Equals(
                lease.HolderProvider,
                expectedHolder.Provider,
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(
                lease.HolderExternalId,
                expectedHolder.ExternalId,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "Legacy reservation holder does not match the authenticated proposed peer authority holder.");
        }
    }

    private static void ValidateStartingHead(
        World world,
        StewardRemoteWorldHead remoteHead)
    {
        if (world.CurrentStateRevisionId != remoteHead.StateRevisionId ||
            world.CurrentEnvironmentRevisionId != remoteHead.EnvironmentRevisionId)
        {
            throw new InvalidOperationException(
                "Local World head does not match the legacy backend canonical head. Sync the exact canonical World before peer-authority migration.");
        }
    }

    private static void ValidateRetiredHead(
        World world,
        StewardLegacyAuthorityRetirementEvidence evidence)
    {
        if (evidence.WorldId != world.Id ||
            world.CurrentStateRevisionId != evidence.StateRevisionId ||
            world.CurrentEnvironmentRevisionId != evidence.EnvironmentRevisionId)
        {
            throw new InvalidOperationException(
                "Local World head does not match the exact backend head frozen by legacy authority retirement. Peer generation 1 remains blocked.");
        }
    }

    private static void ValidateSameRetirement(
        StewardLegacyAuthorityRetirementEvidence acknowledged,
        StewardLegacyAuthorityRetirementEvidence verified)
    {
        if (acknowledged != verified)
        {
            throw new InvalidDataException(
                "Legacy authority retirement read-back did not match the exact acknowledged tombstone.");
        }
    }

    private void ResolveLocalLease(StewardLegacyAuthorityRetirementEvidence evidence)
    {
        var local = _reservations.Get(evidence.WorldId);
        if (local is null)
        {
            return;
        }

        if (local.SessionId != evidence.SessionId || local.Generation != evidence.Generation)
        {
            throw new InvalidDataException(
                "A different local legacy reservation remains registered after backend retirement.");
        }

        if (!_reservations.TryResolve(
                evidence.WorldId,
                evidence.SessionId,
                evidence.Generation))
        {
            throw new InvalidOperationException(
                "Legacy reservation registry changed while finalizing backend retirement.");
        }
    }

    private async Task TryReleaseUnretiredLeaseAsync(
        WorldId worldId,
        UserIdentity user)
    {
        try
        {
            await _legacyCoordinator.ReleaseHostAsync(
                worldId,
                user,
                CancellationToken.None);
        }
        catch
        {
            // The migration is already blocked by the head/identity mismatch. Do not mask the
            // original failure merely because cleanup of a still-legacy reservation also failed.
        }
    }

    private static bool SameUser(UserIdentity left, UserIdentity right)
        => string.Equals(
               left.Provider,
               right.Provider,
               StringComparison.OrdinalIgnoreCase) &&
           string.Equals(
               left.ExternalId,
               right.ExternalId,
               StringComparison.Ordinal);
}
