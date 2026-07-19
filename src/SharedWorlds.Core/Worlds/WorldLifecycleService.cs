using SharedWorlds.Core.Abstractions;
using SharedWorlds.Core.Domain;

namespace SharedWorlds.Core.Worlds;

/// <summary>
/// Owns the game-agnostic local World lifecycle.
/// Game-specific work is delegated entirely to the selected adapter.
/// </summary>
public sealed class WorldLifecycleService(IWorldStorage storage)
{
    public async Task<World> ImportAsync(
        IGameAdapter adapter,
        GameInstallation installation,
        DetectedWorld detectedWorld,
        string worldName,
        UserIdentity owner,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(worldName);

        var worldId = WorldId.New();
        var environmentId = RevisionId.New();
        var stateId = RevisionId.New();

        var environment = await adapter.InspectEnvironmentAsync(
            installation,
            detectedWorld,
            cancellationToken);

        var captured = await adapter.CaptureDetectedWorldAsync(
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

        await storage.StoreEnvironmentRevisionAsync(environmentRevision, cancellationToken);

        await using (var package = File.OpenRead(captured.Package.Path))
        {
            await storage.StoreRevisionAsync(stateRevision, package, cancellationToken);
        }

        await storage.SaveWorldAsync(world, cancellationToken);
        return world;
    }

    public async Task<PreparedWorldContext> PrepareAsync(
        WorldId worldId,
        IGameAdapter adapter,
        GameInstallation installation,
        CancellationToken cancellationToken = default)
    {
        var world = await storage.LoadWorldAsync(worldId, cancellationToken)
            ?? throw new InvalidOperationException($"World '{worldId}' does not exist.");

        if (!string.Equals(world.GameAdapterId, adapter.Id, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"World '{worldId}' belongs to adapter '{world.GameAdapterId}', not '{adapter.Id}'.");
        }

        var environmentRevisionId = world.CurrentEnvironmentRevisionId
            ?? throw new InvalidOperationException($"World '{worldId}' has no environment revision.");

        var stateRevisionId = world.CurrentStateRevisionId
            ?? throw new InvalidOperationException($"World '{worldId}' has no state revision.");

        var environmentRevision = await storage.LoadEnvironmentRevisionAsync(
            worldId,
            environmentRevisionId,
            cancellationToken)
            ?? throw new InvalidOperationException(
                $"Environment revision '{environmentRevisionId}' does not exist.");

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

        await using (var source = await storage.OpenRevisionAsync(
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
                new StatePackage(stateRevisionId.ToString(), materializedPackagePath),
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
        var context = await PrepareAsync(
            worldId,
            adapter,
            installation,
            cancellationToken);

        var session = await adapter.LaunchHostAsync(context.PreparedWorld, cancellationToken);
        await adapter.WaitForSessionEndAsync(session, cancellationToken);

        var captured = await adapter.CaptureStateAsync(context.PreparedWorld, cancellationToken);
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
            await storage.StoreRevisionAsync(revision, package, cancellationToken);
        }

        var updatedWorld = context.World with
        {
            CurrentStateRevisionId = nextRevisionId
        };

        await storage.SaveWorldAsync(updatedWorld, cancellationToken);
        return updatedWorld;
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // Temporary cleanup failure must not invalidate an otherwise prepared World.
        }
        catch (UnauthorizedAccessException)
        {
            // Best-effort cleanup only.
        }
    }
}

public sealed record PreparedWorldContext(World World, PreparedWorld PreparedWorld);
