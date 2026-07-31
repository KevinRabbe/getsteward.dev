using System.Collections.Concurrent;
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
    private readonly ManagedWritableSessionGate _managedSessionGate;
    private readonly IWorldLifecycleObserver _observer;
    private readonly ConcurrentDictionary<WorldId, ActiveHostedSession> _activeHostedSessions = new();

    public WorldLifecycleService(
        IWorldStorage storage,
        IWorldSessionCoordinator sessionCoordinator,
        IWorkspaceRecoveryStore workspaceRecoveryStore)
        : this(
            storage,
            sessionCoordinator,
            workspaceRecoveryStore,
            new ManagedWritableSessionGate(),
            NullWorldLifecycleObserver.Instance)
    {
    }

    public WorldLifecycleService(
        IWorldStorage storage,
        IWorldSessionCoordinator sessionCoordinator,
        IWorkspaceRecoveryStore workspaceRecoveryStore,
        ManagedWritableSessionGate managedSessionGate)
        : this(
            storage,
            sessionCoordinator,
            workspaceRecoveryStore,
            managedSessionGate,
            NullWorldLifecycleObserver.Instance)
    {
    }

    public WorldLifecycleService(
        IWorldStorage storage,
        IWorldSessionCoordinator sessionCoordinator,
        IWorkspaceRecoveryStore workspaceRecoveryStore,
        ManagedWritableSessionGate managedSessionGate,
        IWorldLifecycleObserver observer)
    {
        ArgumentNullException.ThrowIfNull(storage);
        ArgumentNullException.ThrowIfNull(sessionCoordinator);
        ArgumentNullException.ThrowIfNull(workspaceRecoveryStore);
        ArgumentNullException.ThrowIfNull(managedSessionGate);
        ArgumentNullException.ThrowIfNull(observer);
        _storage = storage;
        _sessionCoordinator = sessionCoordinator;
        _workspaceRecoveryStore = workspaceRecoveryStore;
        _managedSessionGate = managedSessionGate;
        _observer = observer;
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
                StatePackageId: captured.Package.Id,
                EnvironmentRevisionId: environmentId);

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

    public Task<PreparedWorldContext> PrepareAsync(
        WorldId worldId,
        IGameAdapter adapter,
        GameInstallation installation,
        CancellationToken cancellationToken = default)
        => PrepareCoreAsync(
            worldId,
            adapter,
            installation,
            mode: null,
            cancellationToken);

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
            ManagedWorldSessionMode.Local,
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
            ManagedWorldSessionMode.Hosted,
            launchSession: adapter.LaunchHostAsync,
            cancellationToken: cancellationToken);

    /// <summary>
    /// Requests the adapter-defined safe stop of the currently active managed host for this World.
    /// This does not capture or commit state; the normal lifecycle remains responsible for waiting
    /// for adapter-observed session end and then advancing the canonical World transaction.
    /// </summary>
    public async Task<bool> RequestHostStopAsync(
        WorldId worldId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_activeHostedSessions.TryGetValue(worldId, out var active))
        {
            return false;
        }

        await active.RequestStopAsync(cancellationToken);
        return true;
    }

    private async Task<PreparedWorldContext> PrepareCoreAsync(
        WorldId worldId,
        IGameAdapter adapter,
        GameInstallation installation,
        ManagedWorldSessionMode? mode,
        CancellationToken cancellationToken,
        Func<World, PreparedWorld, CancellationToken, Task>? registerPreparedWorkspace = null)
    {
        ArgumentNullException.ThrowIfNull(adapter);
        ArgumentNullException.ThrowIfNull(installation);

        Notify(worldId, mode, WorldLifecyclePhase.ResolvingWorld);
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

        if (world.SharingMode == WorldSharingMode.Shared)
        {
            var verification = await adapter.VerifyEnvironmentAsync(
                installation,
                environmentRevision.Manifest,
                cancellationToken);
            if (!verification.IsReady)
            {
                var detail = verification.Issues.Count == 0
                    ? "The adapter could not verify the exact World environment."
                    : string.Join(
                        " ",
                        verification.Issues
                            .Take(3)
                            .Select(static issue => issue.Message));
                throw new EnvironmentReproductionException(
                    adapter.Id,
                    $"shared World '{world.Name}' is not verified ready. {detail} Run Verify/Repair before writable play.");
            }
        }

        var stateRevision = await _storage.LoadStateRevisionAsync(
            worldId,
            stateRevisionId,
            cancellationToken)
            ?? throw new RevisionNotFoundException(
                worldId,
                stateRevisionId,
                "State");

        EnsureAdapterMatches(adapter.Id, stateRevision.AdapterId, "state revision");
        EnsureCurrentStateEnvironmentMatches(world, environmentRevision, stateRevision);

        Notify(worldId, mode, WorldLifecyclePhase.PreparingEnvironment);
        var prepared = (await adapter.PrepareEnvironmentAsync(
            installation,
            environmentRevision.Manifest,
            cancellationToken)) with
        {
            DisplayName = world.Name
        };

        // Writable sessions register cleanup ownership immediately after the adapter creates a
        // workspace. This durable bookkeeping is intentionally not a separate visible lifecycle
        // phase; RegisteringRecovery remains the later promotion to active session responsibility.
        if (registerPreparedWorkspace is not null)
        {
            await registerPreparedWorkspace(world, prepared, CancellationToken.None);
        }

        var materializedPackagePath = Path.Combine(
            Path.GetTempPath(),
            "SharedWorlds",
            "materialized",
            $"{Guid.NewGuid():N}.package");

        try
        {
            Notify(worldId, mode, WorldLifecyclePhase.DownloadingState);
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

            Notify(worldId, mode, WorldLifecyclePhase.RestoringState);
            await adapter.RestoreStateAsync(
                prepared,
                new StatePackage(stateRevision.StatePackageId, materializedPackagePath),
                cancellationToken);

            return new PreparedWorldContext(world, prepared);
        }
        catch (Exception preparationException)
        {
            if (registerPreparedWorkspace is null)
            {
                try
                {
                    await adapter.FinalizePreparedWorldAsync(
                        prepared,
                        PreparedWorldDisposition.Discard,
                        CancellationToken.None);
                }
                catch (Exception cleanupException)
                {
                    throw new InvalidOperationException(
                        "World preparation failed and the prepared workspace could not be discarded safely.",
                        new AggregateException(preparationException, cleanupException));
                }
            }

            throw;
        }
        finally
        {
            TryDelete(materializedPackagePath);
        }
    }

    private async Task<World> ContinueSessionAsync(
        WorldId worldId,
        IGameAdapter adapter,
        GameInstallation installation,
        UserIdentity user,
        ManagedWorldSessionMode mode,
        Func<PreparedWorld, CancellationToken, Task<GameSessionHandle>> launchSession,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(adapter);
        ArgumentNullException.ThrowIfNull(installation);
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(launchSession);

        using var managedSessionLease = _managedSessionGate.Acquire(worldId);
        await EnsureNoUnresolvedWorkspaceResponsibilityAsync(worldId, cancellationToken);

        Notify(worldId, mode, WorldLifecyclePhase.AcquiringReservation);
        try
        {
            await _sessionCoordinator.AcquireHostAsync(worldId, user, cancellationToken);
        }
        catch (Exception exception)
        {
            // Coordinator implementations may throw only after resolving an ambiguous transport
            // outcome and proving that this caller did not acquire writable authority.
            Notify(
                worldId,
                mode,
                WorldLifecyclePhase.Completed,
                $"Writable reservation was not acquired: {exception.Message}");
            throw;
        }

        Exception? operationException = null;
        PreparedWorldContext? context = null;
        PreparedWorld? preparedWorkspace = null;
        WorkspaceRecoveryRecord? workspaceRecord = null;
        var sessionStarted = false;
        var unresolvedResponsibility = false;
        var lifecycleFinalized = false;

        try
        {
            context = await PrepareCoreAsync(
                worldId,
                adapter,
                installation,
                mode,
                cancellationToken,
                registerPreparedWorkspace: async (preparedWorld, prepared, _) =>
                {
                    preparedWorkspace = prepared;
                    var baseStateRevisionId = preparedWorld.CurrentStateRevisionId
                        ?? throw new WorldIntegrityException(
                            worldId,
                            "The canonical state revision disappeared during preparation.");
                    var environmentRevisionId = preparedWorld.CurrentEnvironmentRevisionId
                        ?? throw new WorldIntegrityException(
                            worldId,
                            "The canonical environment revision disappeared during preparation.");

                    var now = DateTimeOffset.UtcNow;
                    workspaceRecord = new WorkspaceRecoveryRecord(
                        Id: WorkspaceId.New(),
                        WorldId: worldId,
                        BaseStateRevisionId: baseStateRevisionId,
                        AdapterId: adapter.Id,
                        WorkingDirectory: prepared.WorkingDirectory,
                        StartedBy: user,
                        CreatedAt: now,
                        UpdatedAt: now,
                        Status: WorkspaceRecoveryStatus.CleanupPending,
                        Reason: "Prepared workspace exists before session launch; cleanup is the only safe recovery action until launch begins.",
                        EnvironmentRevisionId: environmentRevisionId);

                    await _workspaceRecoveryStore.SaveAsync(workspaceRecord, CancellationToken.None);
                });

            workspaceRecord = workspaceRecord is null
                ? throw new InvalidOperationException(
                    "Writable session preparation completed without durable workspace ownership.")
                : workspaceRecord with
                {
                    Status = WorkspaceRecoveryStatus.Active,
                    UpdatedAt = DateTimeOffset.UtcNow,
                    Reason = null
                };
            Notify(worldId, mode, WorldLifecyclePhase.RegisteringRecovery);
            await _workspaceRecoveryStore.SaveAsync(workspaceRecord, cancellationToken);

            Notify(worldId, mode, WorldLifecyclePhase.StartingSession);
            var session = await launchSession(context.PreparedWorld, cancellationToken);
            sessionStarted = true;

            ActiveHostedSession? activeHostedSession = null;
            if (mode == ManagedWorldSessionMode.Hosted)
            {
                activeHostedSession = new ActiveHostedSession(adapter, session);
                if (!_activeHostedSessions.TryAdd(worldId, activeHostedSession))
                {
                    throw new InvalidOperationException(
                        $"World '{worldId}' already has a registered managed host session.");
                }
            }

            Notify(worldId, mode, WorldLifecyclePhase.Running);
            try
            {
                await adapter.WaitForSessionEndAsync(session, cancellationToken);
            }
            finally
            {
                if (activeHostedSession is not null &&
                    _activeHostedSessions.TryGetValue(worldId, out var registered) &&
                    ReferenceEquals(registered, activeHostedSession))
                {
                    _activeHostedSessions.TryRemove(worldId, out _);
                }
            }

            Notify(worldId, mode, WorldLifecyclePhase.WaitingForSafeCapture);
            CapturedState? captured = null;
            try
            {
                Notify(worldId, mode, WorldLifecyclePhase.Capturing);
                captured = await adapter.CaptureStateAsync(context.PreparedWorld, cancellationToken);
                var nextRevisionId = RevisionId.New();

                workspaceRecord = workspaceRecord with
                {
                    CandidateStateRevisionId = nextRevisionId,
                    UpdatedAt = DateTimeOffset.UtcNow
                };
                await _workspaceRecoveryStore.SaveAsync(workspaceRecord, cancellationToken);

                var capturedEnvironmentRevisionId = workspaceRecord.EnvironmentRevisionId
                    ?? throw new WorldIntegrityException(
                        worldId,
                        "The writable workspace lost its exact environment identity before capture.");
                var revision = new StateRevision(
                    Id: nextRevisionId,
                    WorldId: context.World.Id,
                    ParentRevisionId: context.World.CurrentStateRevisionId,
                    CreatedAt: captured.CapturedAt,
                    CreatedBy: user,
                    AdapterId: adapter.Id,
                    StatePackageId: captured.Package.Id,
                    EnvironmentRevisionId: capturedEnvironmentRevisionId);

                Notify(worldId, mode, WorldLifecyclePhase.StoringCandidate);
                await using (var package = File.OpenRead(captured.Package.Path))
                {
                    await _storage.StoreRevisionAsync(revision, package, cancellationToken);
                }

                var updatedWorld = context.World with
                {
                    CurrentStateRevisionId = nextRevisionId
                };

                Notify(worldId, mode, WorldLifecyclePhase.Committing);
                await _storage.SaveWorldAsync(updatedWorld, cancellationToken);

                Notify(worldId, mode, WorldLifecyclePhase.Finalizing);
                lifecycleFinalized = await CompleteSuccessfulWorkspaceAsync(
                    adapter,
                    context.PreparedWorld,
                    workspaceRecord);
                unresolvedResponsibility = !lifecycleFinalized;

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

            if (sessionStarted)
            {
                unresolvedResponsibility = true;
                Notify(worldId, mode, WorldLifecyclePhase.RecoveryNeeded, exception.Message);
            }

            if (context is not null && workspaceRecord is not null)
            {
                unresolvedResponsibility |= await HandleFailedWorkspaceAsync(
                    adapter,
                    context.PreparedWorld,
                    workspaceRecord,
                    sessionStarted,
                    exception);
            }
            else if (preparedWorkspace is not null && workspaceRecord is not null)
            {
                unresolvedResponsibility |= await HandleFailedWorkspaceAsync(
                    adapter,
                    preparedWorkspace,
                    workspaceRecord,
                    sessionStarted: false,
                    exception);
            }
            else if (context is not null)
            {
                var discarded = await TryFinalizePreparedWorldAsync(
                    adapter,
                    context.PreparedWorld,
                    PreparedWorldDisposition.Discard);
                if (!discarded)
                {
                    unresolvedResponsibility = true;
                    Notify(
                        worldId,
                        mode,
                        WorldLifecyclePhase.CleanupPending,
                        "Prepared workspace could not be discarded after recovery registration failed.");
                }
            }
            else if (preparedWorkspace is not null)
            {
                var discarded = await TryFinalizePreparedWorldAsync(
                    adapter,
                    preparedWorkspace,
                    PreparedWorldDisposition.Discard);
                if (!discarded)
                {
                    unresolvedResponsibility = true;
                    Notify(
                        worldId,
                        mode,
                        WorldLifecyclePhase.CleanupPending,
                        "Prepared workspace could not be discarded after pre-launch preparation failed.");
                }
            }

            throw;
        }
        finally
        {
            var reservationReleased = false;
            try
            {
                await _sessionCoordinator.ReleaseHostAsync(
                    worldId,
                    user,
                    CancellationToken.None);
                reservationReleased = true;
            }
            catch (Exception releaseException)
            {
                unresolvedResponsibility = true;
                Notify(
                    worldId,
                    mode,
                    WorldLifecyclePhase.RecoveryNeeded,
                    $"Writable reservation could not be released safely: {releaseException.Message}");

                if (operationException is null)
                {
                    throw;
                }
            }

            if (reservationReleased && !unresolvedResponsibility)
            {
                // Completed means the whole managed responsibility is over: canonical work is
                // finalized (or a pre-launch failure was safely cleaned) and authority is released.
                if (operationException is not null || lifecycleFinalized)
                {
                    Notify(worldId, mode, WorldLifecyclePhase.Completed);
                }
            }
        }
    }

    private async Task EnsureNoUnresolvedWorkspaceResponsibilityAsync(
        WorldId requestedWorldId,
        CancellationToken cancellationToken)
    {
        var unresolved = (await _workspaceRecoveryStore.ListAsync(cancellationToken))
            .Where(record => record.Status is
                WorkspaceRecoveryStatus.Active or
                WorkspaceRecoveryStatus.RecoveryPending or
                WorkspaceRecoveryStatus.CleanupPending)
            .OrderBy(record => record.CreatedAt)
            .ThenBy(record => record.Id.ToString(), StringComparer.Ordinal)
            .FirstOrDefault();

        if (unresolved is null)
        {
            return;
        }

        var phase = unresolved.Status == WorkspaceRecoveryStatus.CleanupPending
            ? WorldLifecyclePhase.CleanupPending
            : WorldLifecyclePhase.RecoveryNeeded;
        var reason =
            $"Cannot start writable World '{requestedWorldId}' while unresolved workspace '{unresolved.Id}' " +
            $"for World '{unresolved.WorldId}' is '{unresolved.Status}'. Resolve that responsibility first.";

        Notify(unresolved.WorldId, mode: null, phase, reason);
        throw new InvalidOperationException(reason);
    }

    private async Task<bool> CompleteSuccessfulWorkspaceAsync(
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
            Notify(record.WorldId, null, WorldLifecyclePhase.CleanupPending, exception.Message);
            await TrySaveWorkspaceStatusAsync(
                record,
                WorkspaceRecoveryStatus.CleanupPending,
                $"Canonical commit succeeded, but workspace cleanup failed: {exception.Message}");
            return false;
        }

        if (await TryRemoveWorkspaceRecordAsync(record.Id))
        {
            return true;
        }

        const string reason =
            "Canonical commit and workspace cleanup succeeded, but recovery-record removal failed.";
        Notify(record.WorldId, null, WorldLifecyclePhase.CleanupPending, reason);
        await TrySaveWorkspaceStatusAsync(
            record,
            WorkspaceRecoveryStatus.CleanupPending,
            reason);
        return false;
    }

    private async Task<bool> HandleFailedWorkspaceAsync(
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
            return true;
        }

        var discarded = await TryFinalizePreparedWorldAsync(
            adapter,
            preparedWorld,
            PreparedWorldDisposition.Discard);

        if (discarded)
        {
            if (await TryRemoveWorkspaceRecordAsync(record.Id))
            {
                return false;
            }

            const string reason =
                "Session never started and workspace discard succeeded, but recovery-record removal failed.";
            Notify(record.WorldId, null, WorldLifecyclePhase.CleanupPending, reason);
            await TrySaveWorkspaceStatusAsync(
                record,
                WorkspaceRecoveryStatus.CleanupPending,
                reason);
            return true;
        }

        Notify(record.WorldId, null, WorldLifecyclePhase.CleanupPending, failure.Message);
        await TrySaveWorkspaceStatusAsync(
            record,
            WorkspaceRecoveryStatus.CleanupPending,
            $"Session never started and the prepared workspace could not be discarded after failure: {failure.Message}");
        return true;
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
            // The previously persisted record remains a conservative recovery signal.
        }
    }

    private async Task<bool> TryRemoveWorkspaceRecordAsync(WorkspaceId workspaceId)
    {
        try
        {
            await _workspaceRecoveryStore.RemoveAsync(
                workspaceId,
                CancellationToken.None);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private sealed class ActiveHostedSession
    {
        private readonly object _gate = new();
        private readonly IGameAdapter _adapter;
        private readonly GameSessionHandle _session;
        private Task? _stopTask;

        public ActiveHostedSession(IGameAdapter adapter, GameSessionHandle session)
        {
            _adapter = adapter;
            _session = session;
        }

        public async Task RequestStopAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            Task stopTask;
            lock (_gate)
            {
                _stopTask ??= _adapter.RequestHostStopAsync(_session, CancellationToken.None);
                stopTask = _stopTask;
            }

            try
            {
                await stopTask.WaitAsync(cancellationToken);
            }
            catch
            {
                if (stopTask.IsFaulted || stopTask.IsCanceled)
                {
                    lock (_gate)
                    {
                        if (ReferenceEquals(_stopTask, stopTask))
                        {
                            _stopTask = null;
                        }
                    }
                }

                throw;
            }
        }
    }

    private void Notify(
        WorldId worldId,
        ManagedWorldSessionMode? mode,
        WorldLifecyclePhase phase,
        string? detail = null)
        => _observer.OnPhaseChanged(new WorldLifecyclePhaseChange(
            worldId,
            mode,
            phase,
            DateTimeOffset.UtcNow,
            detail));

    private static void EnsureCurrentStateEnvironmentMatches(
        World world,
        EnvironmentRevision environmentRevision,
        StateRevision stateRevision)
    {
        if (environmentRevision.WorldId != world.Id)
        {
            throw new WorldIntegrityException(
                world.Id,
                $"The canonical environment revision belongs to a different World '{environmentRevision.WorldId}'.");
        }

        if (stateRevision.WorldId != world.Id)
        {
            throw new WorldIntegrityException(
                world.Id,
                $"The canonical state revision belongs to a different World '{stateRevision.WorldId}'.");
        }

        if (stateRevision.EnvironmentRevisionId is { } stateEnvironmentRevisionId)
        {
            if (stateEnvironmentRevisionId != environmentRevision.Id)
            {
                throw new WorldIntegrityException(
                    world.Id,
                    $"The canonical state revision belongs to environment '{stateEnvironmentRevisionId}', " +
                    $"but the World points to environment '{environmentRevision.Id}'.");
            }

            return;
        }

        // Compatibility for Worlds created before state/environment association existed. A legacy
        // current state may use the current environment only while it is still the original root.
        // Once environment history exists, launch would require guessing which environment created
        // the state, so preparation remains fail-closed before any adapter workspace is created.
        if (environmentRevision.ParentRevisionId is not null)
        {
            throw new WorldIntegrityException(
                world.Id,
                "The canonical state revision predates exact environment linking, but the World " +
                "has changed environments. Safe World will not guess which environment should " +
                "launch this state.");
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
