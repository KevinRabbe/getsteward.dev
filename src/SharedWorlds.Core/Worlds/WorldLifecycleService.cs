using SharedWorlds.Core.Abstractions;
using SharedWorlds.Core.Domain;
using SharedWorlds.Core.Errors;

namespace SharedWorlds.Core.Worlds;

/// <summary>
/// Owns the game-agnostic World lifecycle.
/// Game-specific work is delegated entirely to the selected adapter.
/// </summary>
public sealed class WorldLifecycleService
{
    private readonly IWorldStorage _storage;
    private readonly IWorldSessionCoordinator _sessionCoordinator;
    private readonly IWorkspaceRecoveryStore _workspaceRecoveryStore;

    public WorldLifecycleService(
        IWorldStorage storage,
        IWorldSessionCoordinator sessionCoordinator,
        IWorkspaceRecoveryStore workspaceRecoveryStore)
    {
        ArgumentNullException.ThrowIfNull(storage);
        ArgumentNullException.ThrowIfNull(sessionCoordinator);
        ArgumentNullException.ThrowIfNull(workspaceRecoveryStore);
        _storage = storage;
        _sessionCoordinator = sessionCoordinator;
        _workspaceRecoveryStore = workspaceRecoveryStore;
    }

    public async Task<World> ImportAsync(
        IGameAdapter adapter,
        GameInstallation installation,
        DetectedWorld detectedWorld,
        string worldName,
        UserIdentity owner,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(adapter);
        ArgumentNullException.ThrowIfNull(installation);
        ArgumentNullException.ThrowIfNull(detectedWorld);
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentException.ThrowIfNullOrWhiteSpace(worldName);

        var worldId = WorldId.New();
        var environmentId = RevisionId.New();
        var stateId = RevisionId.New();

        var environment = await adapter.InspectEnvironmentAsync(
            installation,
            detectedWorld,
            cancellationToken);

        EnsureAdapterMatches(adapter.Id, environment.AdapterId, "environment manifest");

        CapturedState? captured = null;
        try
        {
            captured = await adapter.CaptureDetectedWorldAsync(
                installation,
                detectedWorld,
                cancellationToken);

            var environmentRevision = new EnvironmentRevision(
                Id: environmentId,
                WorldId: worldId,
                ParentRevisionId: null,
                CreatedAt: DateTimeOffset.UtcNow,
                CreatedBy: owner,
                Manifest: environment);

            var stateRevision = new StateRevision(
                Id: stateId,
                WorldId: worldId,
                ParentRevisionId: null,
                CreatedAt: captured.CapturedAt,
                CreatedBy: owner,
                AdapterId: adapter.Id,
                StatePackageId: captured.Package.Id);

            var world = new World(
                Id: worldId,
                Name: worldName,
                GameAdapterId: adapter.Id,
                Members: [owner],
                CurrentEnvironmentRevisionId: environmentId,
                CurrentStateRevisionId: stateId)
            {
                SharingMode = WorldSharingMode.LocalOnly
            };

            await _storage.StoreEnvironmentRevisionAsync(environmentRevision, cancellationToken);

            await using (var package = File.OpenRead(captured.Package.Path))
            {
                await _storage.StoreRevisionAsync(stateRevision, package, cancellationToken);
            }

            // The canonical head is written last. If either revision write fails,
            // no World points at an incomplete revision set.
            await _storage.SaveWorldAsync(world, cancellationToken);
            return world;
        }
        finally
        {
            CleanupCapturedState(captured);
        }
    }

    public async Task<World> SetSharingModeAsync(
        WorldId worldId,
        WorldSharingMode sharingMode,
        CancellationToken cancellationToken = default)
    {
        var world = await _storage.LoadWorldAsync(worldId, cancellationToken)
            ?? throw new WorldNotFoundException(worldId);

        var updated = world with { SharingMode = sharingMode };
        await _storage.SaveWorldAsync(updated, cancellationToken);
        return updated;
    }

    public async Task<PreparedWorldContext> PrepareAsync(
        WorldId worldId,
        IGameAdapter adapter,
        GameInstallation installation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(adapter);
        ArgumentNullException.ThrowIfNull(installation);

        var world = await _storage.LoadWorldAsync(worldId, cancellationToken)
            ?? throw new WorldNotFoundException(worldId);

        EnsureAdapterMatches(adapter.Id, world.GameAdapterId, "World");

        var environmentRevisionId = world.CurrentEnvironmentRevisionId
            ?? throw new WorldIntegrityException(worldId, "The canonical environment revision is missing.");

        var stateRevisionId = world.CurrentStateRevisionId
            ?? throw new WorldIntegrityException(worldId, "The canonical state revision is missing.");

        var environmentRevision = await _storage.LoadEnvironmentRevisionAsync(
            worldId,
            environmentRevisionId,
            cancellationToken)
            ?? throw new RevisionNotFoundException(
                worldId,
                environmentRevisionId,
                "Environment");

        EnsureAdapterMatches(
            adapter.Id,
            environmentRevision.Manifest.AdapterId,
            "environment revision");

        var stateRevision = await _storage.LoadStateRevisionAsync(
            worldId,
            stateRevisionId,
            cancellationToken)
            ?? throw new RevisionNotFoundException(
                worldId,
                stateRevisionId,
                "State");

        EnsureAdapterMatches(adapter.Id, stateRevision.AdapterId, "state revision");

        var prepared = await adapter.PrepareEnvironmentAsync(
            installation,
            environmentRevision.Manifest,
            cancellationToken);

        var materializedPackagePath = Path.Combine(
            Path.GetTempPath(),
            "SharedWorlds",
            "materialized",
            $"{Guid.NewGuid():N}.package");

        Directory.CreateDirectory(Path.GetDirectoryName(materializedPackagePath)!);

        await using (var source = await _storage.OpenRevisionAsync(
                         worldId,
                         stateRevisionId,
                         cancellationToken))
        await using (var destination = new FileStream(
                         materializedPackagePath,
                         FileMode.CreateNew,
                         FileAccess.Write,
                         FileShare.None,
                         bufferSize: 1024 * 128,
                         useAsync: true))
        {
            await source.CopyToAsync(destination, cancellationToken);
            await destination.FlushAsync(cancellationToken);
        }

        try
        {
            await adapter.RestoreStateAsync(
                prepared,
                new StatePackage(stateRevision.StatePackageId, materializedPackagePath),
                cancellationToken);
        }
        finally
        {
            TryDelete(materializedPackagePath);
        }

        return new PreparedWorldContext(world, prepared);
    }

    public Task<World> ContinueLocalAsync(
        WorldId worldId,
        IGameAdapter adapter,
        GameInstallation installation,
        UserIdentity user,
        CancellationToken cancellationToken = default)
        => ContinueSessionAsync(
            worldId,
            adapter,
            installation,
            user,
            requireSharedWorld: false,
            launchSession: adapter.LaunchLocalAsync,
            cancellationToken: cancellationToken);

    public Task<World> ContinueAsHostAsync(
        WorldId worldId,
        IGameAdapter adapter,
        GameInstallation installation,
        UserIdentity user,
        CancellationToken cancellationToken = default)
        => ContinueSessionAsync(
            worldId,
            adapter,
            installation,
            user,
            requireSharedWorld: true,
            launchSession: adapter.LaunchHostAsync,
            cancellationToken: cancellationToken);

    private async Task<World> ContinueSessionAsync(
        WorldId worldId,
        IGameAdapter adapter,
        GameInstallation installation,
        UserIdentity user,
        bool requireSharedWorld,
        Func<PreparedWorld, CancellationToken, Task<GameSessionHandle>> launchSession,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(adapter);
        ArgumentNullException.ThrowIfNull(installation);
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(launchSession);

        if (requireSharedWorld)
        {
            var world = await _storage.LoadWorldAsync(worldId, cancellationToken)
                ?? throw new WorldNotFoundException(worldId);
            EnsureShared(world);
        }

        // The current coordinator acts as the canonical-writer lease for both local play
        // and shared hosting. A future distributed implementation may expose richer session modes,
        // but only one canonical session may advance a World at a time.
        await _sessionCoordinator.AcquireHostAsync(worldId, user, cancellationToken);
        Exception? operationException = null;
        PreparedWorldContext? context = null;
        WorkspaceRecoveryRecord? workspaceRecord = null;
        var sessionStarted = false;

        try
        {
            context = await PrepareAsync(
                worldId,
                adapter,
                installation,
                cancellationToken);

            if (requireSharedWorld)
            {
                EnsureShared(context.World);
            }

            var baseStateRevisionId = context.World.CurrentStateRevisionId
                ?? throw new WorldIntegrityException(
                    worldId,
                    "The canonical state revision disappeared during preparation.");

            var now = DateTimeOffset.UtcNow;
            workspaceRecord = new WorkspaceRecoveryRecord(
                Id: WorkspaceId.New(),
                WorldId: worldId,
                BaseStateRevisionId: baseStateRevisionId,
                AdapterId: adapter.Id,
                WorkingDirectory: context.PreparedWorld.WorkingDirectory,
                StartedBy: user,
                CreatedAt: now,
                UpdatedAt: now,
                Status: WorkspaceRecoveryStatus.Active);

            // Register before launch. A hard process/OS crash after this point leaves an
            // Active record that can be surfaced as an interrupted-session candidate.
            await _workspaceRecoveryStore.SaveAsync(workspaceRecord, cancellationToken);

            var session = await launchSession(context.PreparedWorld, cancellationToken);
            sessionStarted = true;
            await adapter.WaitForSessionEndAsync(session, cancellationToken);

            CapturedState? captured = null;
            try
            {
                captured = await adapter.CaptureStateAsync(context.PreparedWorld, cancellationToken);
                var nextRevisionId = RevisionId.New();

                var revision = new StateRevision(
                    Id: nextRevisionId,
                    WorldId: context.World.Id,
                    ParentRevisionId: context.World.CurrentStateRevisionId,
                    CreatedAt: captured.CapturedAt,
                    CreatedBy: user,
                    AdapterId: adapter.Id,
                    StatePackageId: captured.Package.Id);

                await using (var package = File.OpenRead(captured.Package.Path))
                {
                    await _storage.StoreRevisionAsync(revision, package, cancellationToken);
                }

                var updatedWorld = context.World with
                {
                    CurrentStateRevisionId = nextRevisionId
                };

                // Advance the canonical head only after the new immutable revision is durable.
                await _storage.SaveWorldAsync(updatedWorld, cancellationToken);

                await CompleteSuccessfulWorkspaceAsync(
                    adapter,
                    context.PreparedWorld,
                    workspaceRecord);

                return updatedWorld;
            }
            finally
            {
                CleanupCapturedState(captured);
            }
        }
        catch (Exception exception)
        {
            operationException = exception;

            if (context is not null && workspaceRecord is not null)
            {
                await HandleFailedWorkspaceAsync(
                    adapter,
                    context.PreparedWorld,
                    workspaceRecord,
                    sessionStarted,
                    exception);
            }
            else if (context is not null)
            {
                // Recovery registration failed before launch. No gameplay occurred, so this
                // prepared workspace contains only canonical input and can be discarded.
                await TryFinalizePreparedWorldAsync(
                    adapter,
                    context.PreparedWorld,
                    PreparedWorldDisposition.Discard);
            }

            throw;
        }
        finally
        {
            try
            {
                await _sessionCoordinator.ReleaseHostAsync(
                    worldId,
                    user,
                    CancellationToken.None);
            }
            catch when (operationException is not null)
            {
                // Preserve the primary lifecycle failure. Remote coordinators should also
                // use lease expiry so a failed cleanup cannot hold a canonical session forever.
            }
        }
    }

    private static void EnsureShared(World world)
    {
        if (world.SharingMode != WorldSharingMode.Shared)
        {
            throw new WorldSharingRequiredException(world.Id);
        }
    }

    private async Task CompleteSuccessfulWorkspaceAsync(
        IGameAdapter adapter,
        PreparedWorld preparedWorld,
        WorkspaceRecoveryRecord record)
    {
        try
        {
            await adapter.FinalizePreparedWorldAsync(
                preparedWorld,
                PreparedWorldDisposition.Discard,
                CancellationToken.None);
        }
        catch (Exception exception)
        {
            await TrySaveWorkspaceStatusAsync(
                record,
                WorkspaceRecoveryStatus.CleanupPending,
                $"Canonical commit succeeded, but workspace cleanup failed: {exception.Message}");
            return;
        }

        await TryRemoveWorkspaceRecordAsync(record.Id);
    }

    private async Task HandleFailedWorkspaceAsync(
        IGameAdapter adapter,
        PreparedWorld preparedWorld,
        WorkspaceRecoveryRecord record,
        bool sessionStarted,
        Exception failure)
    {
        if (sessionStarted)
        {
            await TrySaveWorkspaceStatusAsync(
                record,
                WorkspaceRecoveryStatus.RecoveryPending,
                $"Session started but canonical commit did not complete: {failure.Message}");

            await TryFinalizePreparedWorldAsync(
                adapter,
                preparedWorld,
                PreparedWorldDisposition.PreserveForRecovery);
            return;
        }

        var discarded = await TryFinalizePreparedWorldAsync(
            adapter,
            preparedWorld,
            PreparedWorldDisposition.Discard);

        if (discarded)
        {
            await TryRemoveWorkspaceRecordAsync(record.Id);
            return;
        }

        await TrySaveWorkspaceStatusAsync(
            record,
            WorkspaceRecoveryStatus.CleanupPending,
            $"Session never started and the prepared workspace could not be discarded after failure: {failure.Message}");
    }

    private async Task<bool> TryFinalizePreparedWorldAsync(
        IGameAdapter adapter,
        PreparedWorld preparedWorld,
        PreparedWorldDisposition disposition)
    {
        try
        {
            await adapter.FinalizePreparedWorldAsync(
                preparedWorld,
                disposition,
                CancellationToken.None);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private async Task TrySaveWorkspaceStatusAsync(
        WorkspaceRecoveryRecord record,
        WorkspaceRecoveryStatus status,
        string reason)
    {
        try
        {
            await _workspaceRecoveryStore.SaveAsync(
                record with
                {
                    Status = status,
                    UpdatedAt = DateTimeOffset.UtcNow,
                    Reason = reason
                },
                CancellationToken.None);
        }
        catch
        {
            // The previously persisted Active record remains a conservative recovery signal.
        }
    }

    private async Task TryRemoveWorkspaceRecordAsync(WorkspaceId workspaceId)
    {
        try
        {
            await _workspaceRecoveryStore.RemoveAsync(
                workspaceId,
                CancellationToken.None);
        }
        catch
        {
            // A stale record is safer than deleting recoverability metadata prematurely.
            // Startup reconciliation can remove records whose workspaces no longer exist.
        }
    }

    private static void EnsureAdapterMatches(
        string expectedAdapterId,
        string actualAdapterId,
        string source)
    {
        if (!string.Equals(expectedAdapterId, actualAdapterId, StringComparison.Ordinal))
        {
            throw new AdapterMismatchException(
                expectedAdapterId,
                actualAdapterId,
                source);
        }
    }

    private static void CleanupCapturedState(CapturedState? captured)
    {
        if (captured?.DeletePackageAfterStore == true)
        {
            TryDelete(captured.Package.Path);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // Best-effort cleanup only.
        }
        catch (UnauthorizedAccessException)
        {
            // Best-effort cleanup only.
        }
    }
}

public sealed record PreparedWorldContext(World World, PreparedWorld PreparedWorld);
