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
}
