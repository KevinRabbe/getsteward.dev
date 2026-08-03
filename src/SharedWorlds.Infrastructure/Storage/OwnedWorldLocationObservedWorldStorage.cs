using SharedWorlds.Core.Abstractions;
using SharedWorlds.Core.Domain;

namespace SharedWorlds.Infrastructure.Storage;

/// <summary>
/// Delegates canonical World storage unchanged and emits one synchronous, non-blocking signal only
/// after a mutation has succeeded. The signal is an acceleration hint; canonical storage remains the
/// sole source of truth and startup catalog reconciliation repairs any crash gap.
/// </summary>
public sealed class OwnedWorldLocationObservedWorldStorage : IWorldStorage
{
    private readonly IWorldStorage _inner;
    private readonly Action _onCanonicalStorageChanged;

    public OwnedWorldLocationObservedWorldStorage(
        IWorldStorage inner,
        Action onCanonicalStorageChanged)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(onCanonicalStorageChanged);
        _inner = inner;
        _onCanonicalStorageChanged = onCanonicalStorageChanged;
    }

    public async Task SaveWorldAsync(
        World world,
        CancellationToken cancellationToken = default)
    {
        await _inner.SaveWorldAsync(world, cancellationToken);
        _onCanonicalStorageChanged();
    }

    public Task<World?> LoadWorldAsync(
        WorldId worldId,
        CancellationToken cancellationToken = default)
        => _inner.LoadWorldAsync(worldId, cancellationToken);

    public Task<IReadOnlyList<World>> ListWorldsAsync(
        CancellationToken cancellationToken = default)
        => _inner.ListWorldsAsync(cancellationToken);

    public async Task<bool> DeleteWorldAsync(
        WorldId worldId,
        CancellationToken cancellationToken = default)
    {
        var deleted = await _inner.DeleteWorldAsync(worldId, cancellationToken);
        if (deleted)
        {
            _onCanonicalStorageChanged();
        }

        return deleted;
    }

    public async Task StoreEnvironmentRevisionAsync(
        EnvironmentRevision revision,
        CancellationToken cancellationToken = default)
    {
        await _inner.StoreEnvironmentRevisionAsync(revision, cancellationToken);
        _onCanonicalStorageChanged();
    }

    public Task<EnvironmentRevision?> LoadEnvironmentRevisionAsync(
        WorldId worldId,
        RevisionId revisionId,
        CancellationToken cancellationToken = default)
        => _inner.LoadEnvironmentRevisionAsync(worldId, revisionId, cancellationToken);

    public async Task StoreRevisionAsync(
        StateRevision revision,
        Stream package,
        CancellationToken cancellationToken = default)
    {
        await _inner.StoreRevisionAsync(revision, package, cancellationToken);
        _onCanonicalStorageChanged();
    }

    public Task<StateRevision?> LoadStateRevisionAsync(
        WorldId worldId,
        RevisionId revisionId,
        CancellationToken cancellationToken = default)
        => _inner.LoadStateRevisionAsync(worldId, revisionId, cancellationToken);

    public Task<Stream> OpenRevisionAsync(
        WorldId worldId,
        RevisionId revisionId,
        CancellationToken cancellationToken = default)
        => _inner.OpenRevisionAsync(worldId, revisionId, cancellationToken);

    public Task<bool> IsRevisionPayloadAvailableAsync(
        WorldId worldId,
        RevisionId revisionId,
        CancellationToken cancellationToken = default)
        => _inner.IsRevisionPayloadAvailableAsync(worldId, revisionId, cancellationToken);

    public Task<long?> GetRevisionPayloadSizeAsync(
        WorldId worldId,
        RevisionId revisionId,
        CancellationToken cancellationToken = default)
        => _inner.GetRevisionPayloadSizeAsync(worldId, revisionId, cancellationToken);

    public async Task<bool> EvictRevisionPayloadAsync(
        WorldId worldId,
        RevisionId revisionId,
        CancellationToken cancellationToken = default)
    {
        var evicted = await _inner.EvictRevisionPayloadAsync(
            worldId,
            revisionId,
            cancellationToken);
        if (evicted)
        {
            _onCanonicalStorageChanged();
        }

        return evicted;
    }
}
