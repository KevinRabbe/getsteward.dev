using SharedWorlds.Core.Abstractions;
using SharedWorlds.Core.Domain;
using SharedWorlds.Core.Worlds;

namespace SharedWorlds.Infrastructure.Remote;

public sealed class StewardPendingSyncRecoveryException : InvalidOperationException
{
    public StewardPendingSyncRecoveryException(string code, string message)
        : base(message)
    {
        Code = code;
    }

    public string Code { get; }
}

/// <summary>
/// Completes a preserved remote writable session after candidate upload or canonical commit became
/// ambiguous. The local recovery record is the write-ahead journal; backend canonical metadata is the
/// truth. This service never overwrites a canonical head that is neither the recorded base nor the
/// recorded candidate.
/// </summary>
public sealed class StewardPendingSyncRecoveryService
{
    private readonly IWorldStorage _storage;
    private readonly StewardWorldSessionCoordinator _coordinator;
    private readonly StewardReservationAbandonClient _abandon;
    private readonly IStewardAccessTokenProvider _accessTokens;
    private readonly StewardWritableReservationRegistry _reservations;
    private readonly IWorkspaceRecoveryStore _recovery;
    private readonly ManagedWritableSessionGate _managedSessionGate;

    public StewardPendingSyncRecoveryService(
        IWorldStorage storage,
        StewardWorldSessionCoordinator coordinator,
        StewardReservationAbandonClient abandon,
        IStewardAccessTokenProvider accessTokens,
        StewardWritableReservationRegistry reservations,
        IWorkspaceRecoveryStore recovery,
        ManagedWritableSessionGate managedSessionGate)
    {
        ArgumentNullException.ThrowIfNull(storage);
        ArgumentNullException.ThrowIfNull(coordinator);
        ArgumentNullException.ThrowIfNull(abandon);
        ArgumentNullException.ThrowIfNull(accessTokens);
        ArgumentNullException.ThrowIfNull(reservations);
        ArgumentNullException.ThrowIfNull(recovery);
        ArgumentNullException.ThrowIfNull(managedSessionGate);

        _storage = storage;
        _coordinator = coordinator;
        _abandon = abandon;
        _accessTokens = accessTokens;
        _reservations = reservations;
        _recovery = recovery;
        _managedSessionGate = managedSessionGate;
    }

    public async Task<World> RetryAsync(
        WorldId worldId,
        IGameAdapter adapter,
        GameInstallation installation,
        UserIdentity user,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(adapter);
        ArgumentNullException.ThrowIfNull(installation);
        ArgumentNullException.ThrowIfNull(user);

        using var managedLease = _managedSessionGate.Acquire(worldId);
        var recovery = await LoadRecoveryAsync(worldId, cancellationToken);
        var candidateId = recovery.CandidateStateRevisionId
            ?? throw new StewardPendingSyncRecoveryException(
                "CandidateUnknown",
                "This recovery record predates candidate journaling, so Steward cannot safely infer which immutable candidate should become canonical.");

        if (!string.Equals(recovery.AdapterId, adapter.Id, StringComparison.Ordinal))
        {
            throw new StewardPendingSyncRecoveryException(
                "AdapterMismatch",
                "The pending recovery belongs to a different game adapter.");
        }

        var world = await LoadWorldAsync(worldId, cancellationToken);
        EnsureAdapter(world, adapter);

        if (world.CurrentStateRevisionId == candidateId)
        {
            await CompleteLocalRecoveryAsync(
                world,
                recovery,
                adapter,
                installation,
                user,
                cancellationToken);
            return world;
        }

        if (world.CurrentStateRevisionId != recovery.BaseStateRevisionId)
        {
            throw HeadDiverged(recovery, world);
        }

        await _coordinator.AcquireHostAsync(worldId, user, cancellationToken);
        try
        {
            var lease = _reservations.Get(worldId)
                ?? throw new StewardPendingSyncRecoveryException(
                    "ReservationMissing",
                    "Steward acquired writable authority but did not retain the exact reservation generation locally.");

            if (lease.StartingHead.StateRevisionId != recovery.BaseStateRevisionId)
            {
                var latest = await LoadWorldAsync(worldId, cancellationToken);
                await AbandonExactLeaseAsync(lease, cancellationToken);
                if (latest.CurrentStateRevisionId == candidateId)
                {
                    await CompleteLocalRecoveryAsync(
                        latest,
                        recovery,
                        adapter,
                        installation,
                        user,
                        cancellationToken);
                    return latest;
                }

                throw HeadDiverged(recovery, latest);
            }

            world = await LoadWorldAsync(worldId, cancellationToken);
            if (world.CurrentStateRevisionId == candidateId)
            {
                await AbandonExactLeaseAsync(lease, cancellationToken);
                await CompleteLocalRecoveryAsync(
                    world,
                    recovery,
                    adapter,
                    installation,
                    user,
                    cancellationToken);
                return world;
            }

            if (world.CurrentStateRevisionId != recovery.BaseStateRevisionId)
            {
                await AbandonExactLeaseAsync(lease, cancellationToken);
                throw HeadDiverged(recovery, world);
            }

            var environmentId = world.CurrentEnvironmentRevisionId
                ?? throw new StewardPendingSyncRecoveryException(
                    "EnvironmentMissing",
                    "The pending shared World no longer has a canonical environment revision.");
            var environment = await _storage.LoadEnvironmentRevisionAsync(
                worldId,
                environmentId,
                cancellationToken)
                ?? throw new StewardPendingSyncRecoveryException(
                    "EnvironmentMissing",
                    "The pending shared World environment revision is unavailable.");

            if (!Directory.Exists(recovery.WorkingDirectory))
            {
                throw new StewardPendingSyncRecoveryException(
                    "WorkspaceMissing",
                    "The preserved recovery workspace is missing, so Steward cannot safely reconstruct an unpublished candidate.");
            }

            var prepared = new PreparedWorld(
                installation,
                recovery.WorkingDirectory,
                environment.Manifest,
                world.Name);
            var candidate = await _storage.LoadStateRevisionAsync(
                worldId,
                candidateId,
                cancellationToken);
            if (candidate is null)
            {
                await RepublishCandidateAsync(
                    world,
                    recovery,
                    candidateId,
                    prepared,
                    adapter,
                    user,
                    cancellationToken);
            }
            else if (!string.Equals(candidate.AdapterId, adapter.Id, StringComparison.Ordinal))
            {
                throw new StewardPendingSyncRecoveryException(
                    "CandidateAdapterMismatch",
                    "The journaled candidate revision belongs to a different game adapter.");
            }

            var updated = world with { CurrentStateRevisionId = candidateId };
            await _storage.SaveWorldAsync(updated, cancellationToken);
            await FinalizeRecoveredWorkspaceAsync(
                updated,
                recovery,
                prepared,
                adapter,
                cancellationToken);
            return updated;
        }
        catch
        {
            // ReleaseHostAsync deliberately keeps an exact remote reservation while this record is
            // RecoveryPending. Successful commit already resolves the registry lease inside storage.
            throw;
        }
        finally
        {
            await _coordinator.ReleaseHostAsync(worldId, user, CancellationToken.None);
        }
    }

    private async Task RepublishCandidateAsync(
        World world,
        WorkspaceRecoveryRecord recovery,
        RevisionId candidateId,
        PreparedWorld prepared,
        IGameAdapter adapter,
        UserIdentity user,
        CancellationToken cancellationToken)
    {
        CapturedState? captured = null;
        try
        {
            captured = await adapter.CaptureStateAsync(prepared, cancellationToken);
            var revision = new StateRevision(
                candidateId,
                world.Id,
                recovery.BaseStateRevisionId,
                captured.CapturedAt,
                user,
                adapter.Id,
                captured.Package.Id);
            await using var package = File.OpenRead(captured.Package.Path);
            await _storage.StoreRevisionAsync(revision, package, cancellationToken);
        }
        finally
        {
            if (captured?.DeletePackageAfterStore == true)
            {
                TryDelete(captured.Package.Path);
            }
        }
    }

    private async Task CompleteLocalRecoveryAsync(
        World world,
        WorkspaceRecoveryRecord recovery,
        IGameAdapter adapter,
        GameInstallation installation,
        UserIdentity user,
        CancellationToken cancellationToken)
    {
        var environmentId = world.CurrentEnvironmentRevisionId
            ?? throw new StewardPendingSyncRecoveryException(
                "EnvironmentMissing",
                "The canonical candidate is committed but its environment revision is missing.");
        var environment = await _storage.LoadEnvironmentRevisionAsync(
            world.Id,
            environmentId,
            cancellationToken)
            ?? throw new StewardPendingSyncRecoveryException(
                "EnvironmentMissing",
                "The canonical candidate is committed but its environment metadata is unavailable.");
        var prepared = new PreparedWorld(
            installation,
            recovery.WorkingDirectory,
            environment.Manifest,
            world.Name);
        await FinalizeRecoveredWorkspaceAsync(
            world,
            recovery,
            prepared,
            adapter,
            cancellationToken);

        // A lost success response may have left a stale process-local lease even though backend
        // commit already resolved the reservation. Once the recovery record is gone, normal release
        // converges that local state through ReservationNoLongerCurrent.
        await _coordinator.ReleaseHostAsync(world.Id, user, CancellationToken.None);
    }

    private async Task FinalizeRecoveredWorkspaceAsync(
        World world,
        WorkspaceRecoveryRecord recovery,
        PreparedWorld prepared,
        IGameAdapter adapter,
        CancellationToken cancellationToken)
    {
        try
        {
            await adapter.FinalizePreparedWorldAsync(
                prepared,
                PreparedWorldDisposition.Discard,
                cancellationToken);
        }
        catch (Exception exception)
        {
            await _recovery.SaveAsync(
                recovery with
                {
                    Status = WorkspaceRecoveryStatus.CleanupPending,
                    UpdatedAt = DateTimeOffset.UtcNow,
                    Reason = $"Canonical candidate '{world.CurrentStateRevisionId}' is committed, but workspace cleanup failed: {exception.Message}"
                },
                CancellationToken.None);
            throw new StewardPendingSyncRecoveryException(
                "CleanupPending",
                "The candidate is canonical, but the preserved workspace still requires cleanup.");
        }

        await _recovery.RemoveAsync(recovery.Id, CancellationToken.None);
    }

    private async Task AbandonExactLeaseAsync(
        StewardWritableReservationLease lease,
        CancellationToken cancellationToken)
    {
        var accessToken = await _accessTokens.GetAccessTokenAsync(cancellationToken);
        var result = await _abandon.AbandonAsync(
            lease.WorldId,
            lease.InstallationId,
            lease.SessionId,
            lease.Generation,
            accessToken,
            cancellationToken);
        if (result is RemoteReservationAbandonStatus.Abandoned or
            RemoteReservationAbandonStatus.NoLongerCurrent)
        {
            _reservations.TryResolve(lease.WorldId, lease.SessionId, lease.Generation);
            return;
        }

        throw new StewardPendingSyncRecoveryException(
            "ReservationReleaseFailed",
            "Steward could not safely release the recovery reservation after detecting a changed canonical head.");
    }

    private async Task<WorkspaceRecoveryRecord> LoadRecoveryAsync(
        WorldId worldId,
        CancellationToken cancellationToken)
    {
        var records = await _recovery.ListAsync(cancellationToken);
        return records
                   .Where(record =>
                       record.WorldId == worldId &&
                       record.Status == WorkspaceRecoveryStatus.RecoveryPending)
                   .OrderBy(record => record.CreatedAt)
                   .ThenBy(record => record.Id.ToString(), StringComparer.Ordinal)
                   .FirstOrDefault()
               ?? throw new StewardPendingSyncRecoveryException(
                   "RecoveryNotFound",
                   "No pending writable recovery exists for this World.");
    }

    private async Task<World> LoadWorldAsync(
        WorldId worldId,
        CancellationToken cancellationToken)
        => await _storage.LoadWorldAsync(worldId, cancellationToken)
           ?? throw new StewardPendingSyncRecoveryException(
               "WorldNotFound",
               "The shared World for this pending recovery is no longer accessible.");

    private static void EnsureAdapter(World world, IGameAdapter adapter)
    {
        if (!string.Equals(world.GameAdapterId, adapter.Id, StringComparison.Ordinal))
        {
            throw new StewardPendingSyncRecoveryException(
                "AdapterMismatch",
                "The pending recovery cannot be completed with a different game adapter.");
        }
    }

    private static StewardPendingSyncRecoveryException HeadDiverged(
        WorkspaceRecoveryRecord recovery,
        World world)
        => new(
            "CanonicalHeadDiverged",
            $"Pending candidate '{recovery.CandidateStateRevisionId}' was based on '{recovery.BaseStateRevisionId}', " +
            $"but the current canonical state is '{world.CurrentStateRevisionId}'. Steward will not overwrite that newer head automatically.");

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Temporary capture cleanup must not hide the recovery result.
        }
    }
}
