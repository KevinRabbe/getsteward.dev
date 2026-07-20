using SharedWorlds.Core.Abstractions;
using SharedWorlds.Core.Domain;
using SharedWorlds.Core.Environment;
using SharedWorlds.Core.Errors;

namespace SharedWorlds.Core.Worlds;

/// <summary>
/// Resolves a World's current immutable environment and delegates device readiness work to its adapter.
/// Repairs are local-device operations only and never advance canonical World revision heads.
/// </summary>
public sealed class WorldEnvironmentService
{
    private readonly IWorldStorage _storage;

    public WorldEnvironmentService(IWorldStorage storage)
    {
        ArgumentNullException.ThrowIfNull(storage);
        _storage = storage;
    }

    public async Task<EnvironmentVerificationReport> VerifyAsync(
        WorldId worldId,
        IGameAdapter adapter,
        GameInstallation installation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(adapter);
        ArgumentNullException.ThrowIfNull(installation);

        var environment = await LoadCurrentEnvironmentAsync(
            worldId,
            adapter,
            cancellationToken);
        return await adapter.VerifyEnvironmentAsync(
            installation,
            environment.Manifest,
            cancellationToken);
    }

    public async Task<EnvironmentRepairResult> RepairAsync(
        WorldId worldId,
        IGameAdapter adapter,
        GameInstallation installation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(adapter);
        ArgumentNullException.ThrowIfNull(installation);

        var environment = await LoadCurrentEnvironmentAsync(
            worldId,
            adapter,
            cancellationToken);
        return await adapter.RepairEnvironmentAsync(
            installation,
            environment.Manifest,
            cancellationToken);
    }

    private async Task<EnvironmentRevision> LoadCurrentEnvironmentAsync(
        WorldId worldId,
        IGameAdapter adapter,
        CancellationToken cancellationToken)
    {
        var world = await _storage.LoadWorldAsync(worldId, cancellationToken)
            ?? throw new WorldNotFoundException(worldId);

        if (!string.Equals(world.GameAdapterId, adapter.Id, StringComparison.Ordinal))
        {
            throw new AdapterMismatchException(adapter.Id, world.GameAdapterId, "World");
        }

        var environmentRevisionId = world.CurrentEnvironmentRevisionId
            ?? throw new WorldIntegrityException(
                worldId,
                "The canonical environment revision is missing.");

        var environment = await _storage.LoadEnvironmentRevisionAsync(
            worldId,
            environmentRevisionId,
            cancellationToken)
            ?? throw new RevisionNotFoundException(
                worldId,
                environmentRevisionId,
                "Environment");

        if (!string.Equals(environment.Manifest.AdapterId, adapter.Id, StringComparison.Ordinal))
        {
            throw new AdapterMismatchException(
                adapter.Id,
                environment.Manifest.AdapterId,
                "environment revision");
        }

        return environment;
    }
}
