using SharedWorlds.Core.Abstractions;
using SharedWorlds.Core.Domain;
using SharedWorlds.Core.Worlds;

namespace SharedWorlds.Infrastructure.Recovery;

public sealed class LocalPendingWorkspaceRecoveryException : InvalidOperationException
{
    public LocalPendingWorkspaceRecoveryException(string code, string message)
        : base(message)
    {
        Code = code;
    }

    public string Code { get; }
}

/// <summary>
/// Reconciles a local-only RecoveryPending workspace against the local canonical World. This is the
/// single-machine analogue of StewardPendingSyncRecoveryService: the journaled base/candidate pair is
/// authoritative recovery evidence and a different current head is never overwritten automatically.
/// </summary>
public sealed class LocalPendingWorkspaceRecoveryService
{
    private readonly IWorldStorage _storage;
    private readonly IWorldSessionCoordinator _coordinator;
    private readonly IWorkspaceRecoveryStore _recovery;
    private readonly ManagedWritableSessionGate _managedSessionGate;

    public LocalPendingWorkspaceRecoveryService(
        IWorldStorage storage,
        IWorldSessionCoordinator coordinator,
        IWorkspaceRecoveryStore recovery,
        ManagedWritableSessionGate managedSessionGate)
    {
        ArgumentNullException.ThrowIfNull(storage);
        ArgumentNullException.ThrowIfNull(coordinator);
        ArgumentNullException.ThrowIfNull(recovery);
        ArgumentNullException.ThrowIfNull(managedSessionGate);
        _storage = storage;
        _coordinator = coordinator;
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
        EnsureAdapter(recovery, adapter);

        if (recovery.CandidateStateRevisionId is null)
        {
            recovery = recovery with
            {
                CandidateStateRevisionId = RevisionId.New(),
                UpdatedAt = DateTimeOffset.UtcNow,
                Reason = recovery.Reason ??
                    "A stable candidate ID was assigned while retrying local interrupted-session recovery."
            };
            await _recovery.SaveAsync(recovery, cancellationToken);
        }

        var candidateId = recovery.CandidateStateRevisionId.Value;
        var world = await LoadWorldAsync(worldId, cancellationToken);
        EnsureAdapter(world, adapter);

        if (world.CurrentStateRevisionId == candidateId)
        {
            await VerifyExistingCandidateAsync(
                worldId,
                candidateId,
                recovery,
                adapter,
                cancellationToken);
            await CompleteCanonicalCandidateAsync(
                world,
                recovery,
                adapter,
                installation,
                cancellationToken);
            return world;
        }

        EnsureExpectedHeads(world, recovery);

        await _coordinator.AcquireHostAsync(worldId, user, cancellationToken);
        try
        {
            world = await LoadWorldAsync(worldId, cancellationToken);
            if (world.CurrentStateRevisionId == candidateId)
            {
                await VerifyExistingCandidateAsync(
                    worldId,
                    candidateId,
                    recovery,
                    adapter,
                    cancellationToken);
                await CompleteCanonicalCandidateAsync(
                    world,
                    recovery,
                    adapter,
                    installation,
                    cancellationToken);
                return world;
            }

            EnsureExpectedHeads(world, recovery);
            var environment = await LoadRecordedEnvironmentAsync(
                world,
                recovery,
                cancellationToken);

            if (!Directory.Exists(recovery.WorkingDirectory))
            {
                throw new LocalPendingWorkspaceRecoveryException(
                    "WorkspaceMissing",
                    "The preserved recovery workspace is missing, so Steward cannot reconstruct the local candidate safely.");
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
                await StoreCandidateAsync(
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
                await VerifyCandidatePackageAsync(worldId, candidateId, cancellationToken);
            }

            var updated = world with { CurrentStateRevisionId = candidateId };
            await _storage.SaveWorldAsync(updated, cancellationToken);
            await FinalizeWorkspaceAsync(
                updated,
                recovery,
                prepared,
                adapter,
                cancellationToken);
            return updated;
        }
        finally
        {
            // The local coordinator has no distributed uncertain-generation state. A durable
            // RecoveryPending journal is sufficient to retry after this process-local role is released.
            await _coordinator.ReleaseHostAsync(worldId, user, CancellationToken.None);
        }
    }

    private async Task StoreCandidateAsync(
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
            var environmentRevisionId = recovery.EnvironmentRevisionId
                ?? throw new LocalPendingWorkspaceRecoveryException(
                    "EnvironmentUnknown",
                    "This recovery record predates exact environment journaling. Steward will preserve the workspace rather than create an unlinked candidate.");
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

    private async Task VerifyExistingCandidateAsync(
        WorldId worldId,
        RevisionId candidateId,
        WorkspaceRecoveryRecord recovery,
        IGameAdapter adapter,
        CancellationToken cancellationToken)
    {
        var candidate = await _storage.LoadStateRevisionAsync(
            worldId,
            candidateId,
            cancellationToken)
            ?? throw new LocalPendingWorkspaceRecoveryException(
                "CandidateMissing",
                "The journaled candidate is canonical, but its immutable revision metadata is missing. Steward will preserve the recovery workspace rather than discard its remaining evidence.");
        EnsureCandidateMatches(candidate, recovery, adapter);
        await VerifyCandidatePackageAsync(worldId, candidateId, cancellationToken);
    }

    private async Task VerifyCandidatePackageAsync(
        WorldId worldId,
        RevisionId candidateId,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var package = await _storage.OpenRevisionAsync(
                worldId,
                candidateId,
                cancellationToken);
            var buffer = new byte[128 * 1024];
            while (await package.ReadAsync(buffer, cancellationToken) > 0)
            {
                // Drain the entire stream so storage-owned end-to-end integrity verification runs.
            }
        }
        catch (Exception exception) when (
            exception is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            throw new LocalPendingWorkspaceRecoveryException(
                "CandidatePackageInvalid",
                $"The journaled candidate '{candidateId}' cannot be verified from local storage: {exception.Message} Steward will preserve the recovery workspace and canonical head.");
        }
    }

    private async Task CompleteCanonicalCandidateAsync(
        World world,
        WorkspaceRecoveryRecord recovery,
        IGameAdapter adapter,
        GameInstallation installation,
        CancellationToken cancellationToken)
    {
        if (!Directory.Exists(recovery.WorkingDirectory))
        {
            await RemoveRecoveryRecordAsync(recovery, cancellationToken);
            return;
        }

        var environment = await LoadRecordedEnvironmentAsync(world, recovery, cancellationToken);
        var prepared = new PreparedWorld(
            installation,
            recovery.WorkingDirectory,
            environment.Manifest,
            world.Name);
        await FinalizeWorkspaceAsync(
            world,
            recovery,
            prepared,
            adapter,
            cancellationToken);
    }

    private async Task FinalizeWorkspaceAsync(
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
            await SaveCleanupPendingAsync(
                recovery,
                $"Canonical candidate '{world.CurrentStateRevisionId}' is committed, but workspace cleanup failed: {exception.Message}");
            throw new LocalPendingWorkspaceRecoveryException(
                "CleanupPending",
                "The recovered candidate is canonical, but its preserved workspace still requires cleanup.");
        }

        await RemoveRecoveryRecordAsync(recovery, cancellationToken);
    }

    private async Task RemoveRecoveryRecordAsync(
        WorkspaceRecoveryRecord recovery,
        CancellationToken cancellationToken)
    {
        try
        {
            await _recovery.RemoveAsync(recovery.Id, cancellationToken);
        }
        catch (Exception exception)
        {
            await SaveCleanupPendingAsync(
                recovery,
                $"Canonical recovery and workspace cleanup are complete, but recovery-journal removal failed: {exception.Message}");
            throw new LocalPendingWorkspaceRecoveryException(
                "CleanupPending",
                "The recovered World is canonical, but Steward could not clear its cleanup journal.");
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
            // Preserve the previous durable RecoveryPending record if the status update itself fails.
        }
    }

    private async Task<EnvironmentRevision> LoadRecordedEnvironmentAsync(
        World world,
        WorkspaceRecoveryRecord recovery,
        CancellationToken cancellationToken)
    {
        var environmentId = recovery.EnvironmentRevisionId
            ?? throw new LocalPendingWorkspaceRecoveryException(
                "EnvironmentUnknown",
                "This recovery record predates exact environment journaling. Steward will preserve the workspace rather than guess its environment.");
        if (world.CurrentEnvironmentRevisionId != environmentId)
        {
            throw new LocalPendingWorkspaceRecoveryException(
                "EnvironmentHeadDiverged",
                $"The recovery workspace belongs to environment '{environmentId}', but the current World uses '{world.CurrentEnvironmentRevisionId}'. Steward will not combine them automatically.");
        }

        var environment = await _storage.LoadEnvironmentRevisionAsync(
            world.Id,
            environmentId,
            cancellationToken)
            ?? throw new LocalPendingWorkspaceRecoveryException(
                "EnvironmentMissing",
                "The exact environment revision for this recovery workspace is unavailable.");
        if (!string.Equals(environment.Manifest.AdapterId, recovery.AdapterId, StringComparison.Ordinal))
        {
            throw new LocalPendingWorkspaceRecoveryException(
                "EnvironmentAdapterMismatch",
                "The recovery workspace environment belongs to a different game adapter.");
        }

        return environment;
    }

    private static void EnsureExpectedHeads(World world, WorkspaceRecoveryRecord recovery)
    {
        if (world.CurrentStateRevisionId != recovery.BaseStateRevisionId)
        {
            throw new LocalPendingWorkspaceRecoveryException(
                "CanonicalHeadDiverged",
                $"The recovery candidate was based on '{recovery.BaseStateRevisionId}', but the current canonical state is '{world.CurrentStateRevisionId}'. Steward will not overwrite that different head automatically.");
        }

        if (recovery.EnvironmentRevisionId is { } environmentId &&
            world.CurrentEnvironmentRevisionId != environmentId)
        {
            throw new LocalPendingWorkspaceRecoveryException(
                "EnvironmentHeadDiverged",
                $"The recovery workspace belongs to environment '{environmentId}', but the current World uses '{world.CurrentEnvironmentRevisionId}'. Steward will not combine them automatically.");
        }
    }

    private static void EnsureCandidateMatches(
        StateRevision candidate,
        WorkspaceRecoveryRecord recovery,
        IGameAdapter adapter)
    {
        if (!string.Equals(candidate.AdapterId, adapter.Id, StringComparison.Ordinal))
        {
            throw new LocalPendingWorkspaceRecoveryException(
                "CandidateAdapterMismatch",
                "The journaled recovery candidate belongs to a different game adapter.");
        }

        if (candidate.ParentRevisionId != recovery.BaseStateRevisionId)
        {
            throw new LocalPendingWorkspaceRecoveryException(
                "CandidateParentMismatch",
                "The journaled recovery candidate does not descend from the recorded starting revision.");
        }

        if (candidate.EnvironmentRevisionId != recovery.EnvironmentRevisionId)
        {
            throw new LocalPendingWorkspaceRecoveryException(
                "CandidateEnvironmentMismatch",
                "The journaled recovery candidate does not belong to the exact environment recorded for its workspace.");
        }
    }

    private static void EnsureAdapter(WorkspaceRecoveryRecord recovery, IGameAdapter adapter)
    {
        if (!string.Equals(recovery.AdapterId, adapter.Id, StringComparison.Ordinal))
        {
            throw new LocalPendingWorkspaceRecoveryException(
                "AdapterMismatch",
                "The pending recovery belongs to a different game adapter.");
        }
    }

    private static void EnsureAdapter(World world, IGameAdapter adapter)
    {
        if (!string.Equals(world.GameAdapterId, adapter.Id, StringComparison.Ordinal))
        {
            throw new LocalPendingWorkspaceRecoveryException(
                "AdapterMismatch",
                "The pending recovery cannot be completed with a different game adapter.");
        }
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
               ?? throw new LocalPendingWorkspaceRecoveryException(
                   "RecoveryNotFound",
                   "No pending local recovery exists for this World.");
    }

    private async Task<World> LoadWorldAsync(
        WorldId worldId,
        CancellationToken cancellationToken)
        => await _storage.LoadWorldAsync(worldId, cancellationToken)
           ?? throw new LocalPendingWorkspaceRecoveryException(
               "WorldNotFound",
               "The local World for this pending recovery is unavailable.");

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
