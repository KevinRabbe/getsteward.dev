using SharedWorlds.Core.Domain;

namespace SharedWorlds.Core.Abstractions;

/// <summary>
/// Durable storage boundary. Implementations may be local filesystem, Steam-backed, or another backend.
/// Core does not know which one is in use.
/// </summary>
public interface IWorldStorage
{
    Task SaveWorldAsync(World world, CancellationToken cancellationToken = default);
    Task<World?> LoadWorldAsync(WorldId worldId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<World>> ListWorldsAsync(CancellationToken cancellationToken = default)
        => throw new NotSupportedException("This World storage backend does not support catalog listing.");

    Task<bool> DeleteWorldAsync(WorldId worldId, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("This World storage backend does not support World deletion.");

    Task StoreEnvironmentRevisionAsync(
        EnvironmentRevision revision,
        CancellationToken cancellationToken = default);

    Task<EnvironmentRevision?> LoadEnvironmentRevisionAsync(
        WorldId worldId,
        RevisionId revisionId,
        CancellationToken cancellationToken = default);

    Task StoreRevisionAsync(
        StateRevision revision,
        Stream package,
        CancellationToken cancellationToken = default);

    Task<StateRevision?> LoadStateRevisionAsync(
        WorldId worldId,
        RevisionId revisionId,
        CancellationToken cancellationToken = default);

    Task<Stream> OpenRevisionAsync(
        WorldId worldId,
        RevisionId revisionId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Reports whether the large restorable payload for an immutable state revision is locally
    /// available. Revision metadata and parent links may remain available after payload eviction.
    /// </summary>
    Task<bool> IsRevisionPayloadAvailableAsync(
        WorldId worldId,
        RevisionId revisionId,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException(
            "This World storage backend does not expose state-payload availability.");

    /// <summary>
    /// Returns the exact stored payload length without opening or materializing the payload. Null
    /// means the payload is absent. Implementations must not count immutable revision metadata.
    /// </summary>
    Task<long?> GetRevisionPayloadSizeAsync(
        WorldId worldId,
        RevisionId revisionId,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException(
            "This World storage backend does not expose state-payload sizes.");

    /// <summary>
    /// Removes only the large restorable payload for an immutable state revision. Implementations
    /// must preserve revision metadata and parent links. Returns false when the payload was already
    /// absent. Core is responsible for proving that the revision is not current or checkpointed.
    /// </summary>
    Task<bool> EvictRevisionPayloadAsync(
        WorldId worldId,
        RevisionId revisionId,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException(
            "This World storage backend does not support state-payload eviction.");
}
