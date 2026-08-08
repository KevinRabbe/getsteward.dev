using SharedWorlds.Core.Abstractions;
using SharedWorlds.Core.Domain;

namespace SharedWorlds.Infrastructure.Sessions;

/// <summary>
/// External proof boundary for the one-time transition from the legacy shared-World authority model
/// to peer authority generation 1. The migration service never infers authorization from local file
/// possession, World membership alone, or the user clicking Host.
/// </summary>
public interface IPeerWorldAuthorityMigrationAuthorizer
{
    Task<bool> CanInitializePeerAuthorityAsync(
        World world,
        UserIdentity proposedHolder,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Performs the fail-closed one-time migration of an existing shared World into persistent peer
/// authority. The account fence is committed before World.PeerAuthority becomes visible, so an
/// interruption can leave only a safe retry marker rather than an unfenced writable World.
/// </summary>
public sealed class PeerWorldAuthorityMigrationService
{
    private const ulong InitialGeneration = 1;

    private readonly IWorldStorage _storage;
    private readonly IPeerAuthorityFenceStore _authorityFences;
    private readonly IPeerWorldAuthorityMigrationAuthorizer _authorizer;

    public PeerWorldAuthorityMigrationService(
        IWorldStorage storage,
        IPeerAuthorityFenceStore authorityFences,
        IPeerWorldAuthorityMigrationAuthorizer authorizer)
    {
        ArgumentNullException.ThrowIfNull(storage);
        ArgumentNullException.ThrowIfNull(authorityFences);
        ArgumentNullException.ThrowIfNull(authorizer);
        _storage = storage;
        _authorityFences = authorityFences;
        _authorizer = authorizer;
    }

    public async Task<World> InitializeAsync(
        WorldId worldId,
        UserIdentity proposedHolder,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(proposedHolder);
        var world = await _storage.LoadWorldAsync(worldId, cancellationToken)
            ?? throw new InvalidDataException(
                $"Cannot migrate missing World '{worldId}' to peer authority.");
        if (world.SharingMode != WorldSharingMode.Shared)
        {
            throw new InvalidOperationException(
                $"World '{worldId}' is local-only and does not need shared peer authority.");
        }

        var stateRevision = world.CurrentStateRevisionId
            ?? throw new InvalidDataException(
                $"World '{worldId}' has no canonical state revision to fence during peer-authority migration.");
        if (!ContainsStableMember(world.Members, proposedHolder))
        {
            throw new InvalidOperationException(
                $"Proposed peer authority holder '{proposedHolder.ExternalId}' is not a canonical World member.");
        }

        if (world.PeerAuthority is { } existingAuthority)
        {
            if (existingAuthority.Generation != InitialGeneration ||
                !SameUser(existingAuthority.Holder, proposedHolder))
            {
                throw new InvalidOperationException(
                    $"World '{worldId}' already has a different persistent peer authority assignment.");
            }

            var existingFence = await _authorityFences.LoadAsync(
                worldId,
                cancellationToken);
            if (!MatchesInitialActiveFence(
                    existingFence,
                    worldId,
                    proposedHolder,
                    stateRevision))
            {
                throw new InvalidDataException(
                    $"World '{worldId}' has peer authority generation 1 but the local durable account fence is missing or inconsistent.");
            }

            return world;
        }

        if (!await _authorizer.CanInitializePeerAuthorityAsync(
                world,
                proposedHolder,
                cancellationToken))
        {
            throw new UnauthorizedAccessException(
                $"Peer authority migration was not authorized for World '{worldId}' and identity '{proposedHolder.ExternalId}'.");
        }

        var fence = await _authorityFences.LoadAsync(worldId, cancellationToken);
        if (fence is null)
        {
            await _authorityFences.SaveAsync(
                new PeerAuthorityFence(
                    worldId,
                    proposedHolder,
                    InitialGeneration,
                    stateRevision,
                    PeerAuthorityFenceState.Active,
                    DateTimeOffset.UtcNow),
                cancellationToken);
        }
        else if (!MatchesInitialActiveFence(
                     fence,
                     worldId,
                     proposedHolder,
                     stateRevision))
        {
            throw new InvalidDataException(
                $"World '{worldId}' already has a conflicting durable account authority fence and cannot be initialized automatically.");
        }

        var migrated = world with
        {
            PeerAuthority = new WorldPeerAuthority(
                proposedHolder,
                InitialGeneration)
        };
        await _storage.SaveWorldAsync(migrated, cancellationToken);
        return migrated;
    }

    private static bool MatchesInitialActiveFence(
        PeerAuthorityFence? fence,
        WorldId worldId,
        UserIdentity holder,
        RevisionId stateRevision)
        => fence is not null &&
           fence.WorldId == worldId &&
           fence.State == PeerAuthorityFenceState.Active &&
           fence.Generation == InitialGeneration &&
           fence.StateRevisionId == stateRevision &&
           SameUser(fence.Holder, holder);

    private static bool ContainsStableMember(
        IReadOnlyList<UserIdentity> members,
        UserIdentity expected)
        => members.Any(member => SameUser(member, expected));

    private static bool SameUser(UserIdentity left, UserIdentity right)
        => string.Equals(left.Provider, right.Provider, StringComparison.OrdinalIgnoreCase) &&
           string.Equals(left.ExternalId, right.ExternalId, StringComparison.Ordinal);
}
