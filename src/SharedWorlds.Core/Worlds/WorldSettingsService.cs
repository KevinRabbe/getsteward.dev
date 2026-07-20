using SharedWorlds.Core.Abstractions;
using SharedWorlds.Core.Domain;
using SharedWorlds.Core.Errors;

namespace SharedWorlds.Core.Worlds;

/// <summary>
/// Owns explicit user-controlled policy changes for an existing World.
/// These settings never mutate immutable EnvironmentRevision or StateRevision records.
/// </summary>
public sealed class WorldSettingsService
{
    private readonly IWorldStorage _storage;

    public WorldSettingsService(IWorldStorage storage)
    {
        ArgumentNullException.ThrowIfNull(storage);
        _storage = storage;
    }

    public async Task<World> SetGameVersionPolicyAsync(
        WorldId worldId,
        WorldGameVersionPolicy gameVersionPolicy,
        CancellationToken cancellationToken = default)
    {
        var world = await _storage.LoadWorldAsync(worldId, cancellationToken)
            ?? throw new WorldNotFoundException(worldId);

        var updated = world with { GameVersionPolicy = gameVersionPolicy };
        await _storage.SaveWorldAsync(updated, cancellationToken);
        return updated;
    }
}
