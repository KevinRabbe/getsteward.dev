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
/// recorded candidate, and never reconstructs a preserved workspace under a different environment.
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
            await CompleteCanonicalCandidateAsync(
                world,
                recovery,
                adapter,
                installation,
                user,
                cancellationToken);
            return world;
        }

        EnsureBaseHeadMatches(world, recovery);
        var recordedEnvironmentId = RequireRecordedEnvironmentId(recovery);

        await _coordinator.AcquireHostAsync(worldId, user, cancellationToken);
        try
        {
            var lease = _reservations.Get(worldId)
                ?? throw new StewardPendingSyncRecoveryException(
                    "ReservationMissing",
                    "Steward acquired writable authority but did not retain the exact reservation generation locally.");

            if (lease.StartingHead.StateRevisionId != recovery.BaseStateRevisionId ||
                lease.StartingHead.EnvironmentRevisionId != recordedEnvironmentId)
            {
                var latest = await LoadWorldAsync(worldId, cancellationToken);
                await AbandonExactLeaseAsync(lease, cancellationToken);
                if (latest.CurrentStateRevisionId == candidateId)
                {
                    await CompleteCanonicalCandidateAsync(
                        latest,
                        recovery,
                        adapter,
                        installation,
                        user,
                        cancellationToken);
                    return latest;
                }

                EnsureBaseHeadMatches(latest, recovery);
                throw new StewardPendingSyncRecoveryException(
                    "ReservationHeadMismatch",
                    "The writable reservation was acquired against a different World head than the interrupted workspace. Steward released it instead of combining different state or environment histories.");
            }

            world = await LoadWorldAsync(worldId, cancellationToken);
            if (world.CurrentStateRevisionId == candidateId)
            {
                await AbandonExactLeaseAsync(lease, cancellationToken);
                await CompleteCanonicalCandidateAsync(
                    world,
                    recovery,
                    adapter,
                    installation,
                    user,
                    cancellationToken);
                return world;
            }

            try
            {
                EnsureBaseHeadMatches(world, recovery);
            }
            catch
            {
                await AbandonExactLeaseAsync(lease, cancellationToken);
                throw;
            }

            var environment = await LoadRecordedEnvironmentAsync(
                worldId,
                recovery,
                adapter,
                cancellationToken);
            var candidate = await _storage.LoadStateRevisionAsync(
                worldId,
                candidateId,
                cancellationToken);

            PreparedWorld? prepared = null;
            if (candidate is null)
            {
                if (!Directory.Exists(recovery.WorkingDirectory))
                {
                    throw new StewardPendingSyncRecoveryException(
                        "WorkspaceMissing",
                        "The preserved recovery workspace is missing, so Steward cannot reconstruct an unpublished candidate safely.");
                }

                prepared = new PreparedWorld(
                    installation,
                    recovery.WorkingDirectory,
                    environment.Manifest,
                    world.Name);
                await RepublishCandidateAsync(
                    world,
                    recovery,
                    candidateId,
                    prepared,
                    adapter,
                    user,
                    cancellationToken);
            }
            else
            {
                EnsureCandidateMatches(candidate, recovery, adapter);
            }

            var updated = world with { CurrentStateRevisionId = candidateId };
            await _storage.SaveWorldAsync(updated, cancellationToken);

            if (prepared is null && Directory.Exists(recovery.WorkingDirectory))
            {
                prepared = new PreparedWorld(
                    installation,
                    recovery.WorkingDirectory,
                    environment.Manifest,
                    world.Name);
            }

            await CompleteCommittedRecoveryAsync(
                updated,
                recovery,
                prepared,
                adapter,
                cancellationToken);
            return updated;
        }
        finally
        {
            // ReleaseHostAsync deliberately keeps an exact remote reservation while this record is
            // still RecoveryPending. Successful commit resolves the registry lease inside storage;
            // explicit divergence paths abandon their exact lease before reaching this point.
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
            var environmentRevisionId = RequireRecordedEnvironmentId(recovery);
            var revision = new StateRevision(
                candidateId,
                world.Id,
                recovery.BaseStateRevisionId,
                captured.CapturedAt,
                user,
                adapter.Id,
                captured.Package.Id,
                EnvironmentRevisionId: environmentRevisionId);
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

    private async Task CompleteCanonicalCandidateAsync(
        World world,
        WorkspaceRecoveryRecord recovery,
        IGameAdapter adapter,
        GameInstallation installation,
        UserIdentity user,
        CancellationToken cancellationToken)
    {
        PreparedWorld? prepared = null;
        if (Directory.Exists(recovery.WorkingDirectory))
        {
            var environment = await LoadRecordedEnvironmentAsync(
                world.Id,
                recovery,
                adapter,
                cancellationToken);
            prepared = new PreparedWorld(
                installation,
                recovery.WorkingDirectory,
                environment.Manifest,
                world.Name);
        }

        await CompleteCommittedRecoveryAsync(
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

    private async Task CompleteCommittedRecoveryAsync(
        World world,
        WorkspaceRecoveryRecord recovery,
        PreparedWorld? prepared,
        IGameAdapter adapter,
        CancellationToken cancellationToken)
    {
        if (prepared is not null)
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
                await SaveCleanupPendingAsync(
                    recovery,
                    $"Canonical candidate '{world.CurrentStateRevisionId}' is committed, but workspace cleanup failed: {exception.Message}");
                throw new StewardPendingSyncRecoveryException(
                    "CleanupPending",
                    "The candidate is canonical, but the preserved workspace still requires cleanup.");
            }
        }

        try
        {
            await _recovery.RemoveAsync(recovery.Id, CancellationToken.None);
        }
        catch (Exception exception)
        {
            await SaveCleanupPendingAsync(
                recovery,
                $"Canonical candidate '{world.CurrentStateRevisionId}' and workspace cleanup are complete, but recovery-journal removal failed: {exception.Message}");
            throw new StewardPendingSyncRecoveryException(
                "CleanupPending",
                "The candidate is canonical, but Steward could not clear its cleanup journal.");
        }
    }

    private async Task SaveCleanupPendingAsync(
        WorkspaceRecoveryRecord recovery,
        string reason)
    {
        try
        {
            await _recovery.SaveAsync(
                recovery with
                {
                    Status = WorkspaceRecoveryStatus.CleanupPending,
                    UpdatedAt = DateTimeOffset.UtcNow,
                    Reason = reason
                },
                CancellationToken.None);
        }
        catch
        {
            // Preserve the previous durable recovery record if the status update itself fails.
        }
    }

    private async Task<EnvironmentRevision> LoadRecordedEnvironmentAsync(
        WorldId worldId,
        WorkspaceRecoveryRecord recovery,
        IGameAdapter adapter,
        CancellationToken cancellationToken)
    {
        var environmentId = RequireRecordedEnvironmentId(recovery);
        var environment = await _storage.LoadEnvironmentRevisionAsync(
            worldId,
            environmentId,
            cancellationToken)
            ?? throw new StewardPendingSyncRecoveryException(
                "EnvironmentMissing",
                "The exact environment revision recorded for this recovery workspace is unavailable.");
        if (!string.Equals(environment.Manifest.AdapterId, adapter.Id, StringComparison.Ordinal))
        {
            throw new StewardPendingSyncRecoveryException(
                "EnvironmentAdapterMismatch",
                "The journaled recovery environment belongs to a different game adapter.");
        }

        return environment;
    }

    private static RevisionId RequireRecordedEnvironmentId(WorkspaceRecoveryRecord recovery)
        => recovery.EnvironmentRevisionId
           ?? throw new StewardPendingSyncRecoveryException(
               "EnvironmentUnknown",
               "This recovery record predates exact environment journaling. Steward will preserve the workspace rather than guess which environment created it.");

    private static void EnsureBaseHeadMatches(World world, WorkspaceRecoveryRecord recovery)
    {
        if (world.CurrentStateRevisionId != recovery.BaseStateRevisionId)
        {
            throw HeadDiverged(recovery, world);
        }

        var environmentId = RequireRecordedEnvironmentId(recovery);
        if (world.CurrentEnvironmentRevisionId != environmentId)
        {
            throw new StewardPendingSyncRecoveryException(
                "EnvironmentHeadDiverged",
                $"The interrupted workspace belongs to environment '{environmentId}', but the current canonical environment is '{world.CurrentEnvironmentRevisionId}'. Steward will not combine them automatically.");
        }
    }

    private static void EnsureCandidateMatches(
        StateRevision candidate,
        WorkspaceRecoveryRecord recovery,
        IGameAdapter adapter)
    {
        if (!string.Equals(candidate.AdapterId, adapter.Id, StringComparison.Ordinal))
        {
            throw new StewardPendingSyncRecoveryException(
                "CandidateAdapterMismatch",
                "The journaled candidate revision belongs to a different game adapter.");
        }

        if (candidate.ParentRevisionId != recovery.BaseStateRevisionId)
        {
            throw new StewardPendingSyncRecoveryException(
                "CandidateParentMismatch",
                "The journaled candidate revision does not descend from the recorded starting revision.");
        }

        if (candidate.EnvironmentRevisionId != recovery.EnvironmentRevisionId)
        {
            throw new StewardPendingSyncRecoveryException(
                "CandidateEnvironmentMismatch",
                "The journaled candidate revision does not belong to the exact environment recorded for its workspace.");
        }
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
