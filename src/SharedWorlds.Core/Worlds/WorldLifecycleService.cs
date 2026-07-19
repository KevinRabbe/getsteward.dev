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

    public WorldLifecycleService(
        IWorldStorage storage,
        IWorldSessionCoordinator sessionCoordinator)
    {
        ArgumentNullException.ThrowIfNull(storage);
        ArgumentNullException.ThrowIfNull(sessionCoordinator);
        _storage = storage;
        _sessionCoordinator = sessionCoordinator;
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
                CurrentStateRevisionId: stateId);

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
            ?? throw new InvalidOperationException($"World '{worldId}' has no environment revision.");

        var stateRevisionId = world.CurrentStateRevisionId
            ?? throw new InvalidOperationException($"World '{worldId}' has no state revision.");

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

    public async Task<World> ContinueAsHostAsync(
        WorldId worldId,
        IGameAdapter adapter,
        GameInstallation installation,
        UserIdentity user,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(adapter);
        ArgumentNullException.ThrowIfNull(installation);
        ArgumentNullException.ThrowIfNull(user);

        await _sessionCoordinator.AcquireHostAsync(worldId, user, cancellationToken);
        Exception? operationException = null;

        try
        {
            var context = await PrepareAsync(
                worldId,
                adapter,
                installation,
                cancellationToken);

            var session = await adapter.LaunchHostAsync(context.PreparedWorld, cancellationToken);
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
                // use lease expiry so a failed cleanup cannot hold a host forever.
            }
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
