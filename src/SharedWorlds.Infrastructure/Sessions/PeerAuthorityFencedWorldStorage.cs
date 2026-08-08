using SharedWorlds.Core.Abstractions;
using SharedWorlds.Core.Domain;

namespace SharedWorlds.Infrastructure.Sessions;

/// <summary>
/// Decorates local World storage for peer-hosted authority. Immutable revision bytes/metadata are
/// stored through the underlying storage as usual. When the current local peer authority publishes a
/// new canonical state head at the same authority generation, this decorator advances the durable
/// Active authority fence first and only then publishes the World head.
///
/// That ordering is intentional. If the fence write fails, the old World head remains canonical. If
/// the World-head write fails after the fence advanced, the account is conservatively fenced ahead and
/// cannot restart the stale head; retrying the exact same direct-child publication is idempotent.
/// </summary>
public sealed class PeerAuthorityFencedWorldStorage : IWorldStorage
{
    private readonly IWorldStorage _inner;
    private readonly IPeerAuthorityActiveRevisionFenceStore _authorityFences;
    private readonly UserIdentity _localUser;

    public PeerAuthorityFencedWorldStorage(
        IWorldStorage inner,
        IPeerAuthorityActiveRevisionFenceStore authorityFences,
        UserIdentity localUser)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(authorityFences);
        ArgumentNullException.ThrowIfNull(localUser);
        _inner = inner;
        _authorityFences = authorityFences;
        _localUser = localUser;
    }

    public async Task SaveWorldAsync(
        World world,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(world);
        var current = await _inner.LoadWorldAsync(world.Id, cancellationToken);
        if (current is not null)
        {
            await FenceActiveRevisionAdvanceIfRequiredAsync(
                current,
                world,
                cancellationToken);
        }

        await _inner.SaveWorldAsync(world, cancellationToken);
    }

    public Task<World?> LoadWorldAsync(
        WorldId worldId,
        CancellationToken cancellationToken = default)
        => _inner.LoadWorldAsync(worldId, cancellationToken);

    public Task<IReadOnlyList<World>> ListWorldsAsync(
        CancellationToken cancellationToken = default)
        => _inner.ListWorldsAsync(cancellationToken);

    public Task<bool> DeleteWorldAsync(
        WorldId worldId,
        CancellationToken cancellationToken = default)
        => _inner.DeleteWorldAsync(worldId, cancellationToken);

    public Task StoreEnvironmentRevisionAsync(
        EnvironmentRevision revision,
        CancellationToken cancellationToken = default)
        => _inner.StoreEnvironmentRevisionAsync(revision, cancellationToken);

    public Task<EnvironmentRevision?> LoadEnvironmentRevisionAsync(
        WorldId worldId,
        RevisionId revisionId,
        CancellationToken cancellationToken = default)
        => _inner.LoadEnvironmentRevisionAsync(worldId, revisionId, cancellationToken);

    public Task StoreRevisionAsync(
        StateRevision revision,
        Stream package,
        CancellationToken cancellationToken = default)
        => _inner.StoreRevisionAsync(revision, package, cancellationToken);

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

    public Task<bool> EvictRevisionPayloadAsync(
        WorldId worldId,
        RevisionId revisionId,
        CancellationToken cancellationToken = default)
        => _inner.EvictRevisionPayloadAsync(worldId, revisionId, cancellationToken);

    private async Task FenceActiveRevisionAdvanceIfRequiredAsync(
        World current,
        World next,
        CancellationToken cancellationToken)
    {
        if (current.SharingMode != WorldSharingMode.Shared ||
            next.SharingMode != WorldSharingMode.Shared ||
            current.PeerAuthority is not { } currentAuthority ||
            next.PeerAuthority is not { } nextAuthority ||
            !SameUser(currentAuthority.Holder, _localUser) ||
            !SameUser(nextAuthority.Holder, _localUser))
        {
            return;
        }

        if (currentAuthority.Generation == 0 ||
            nextAuthority.Generation == 0)
        {
            throw new InvalidDataException(
                $"World '{next.Id}' has invalid zero peer-authority generation.");
        }

        if (currentAuthority.Generation != nextAuthority.Generation)
        {
            throw new InvalidDataException(
                $"World '{next.Id}' cannot change peer-authority generation while retaining the same local holder through a normal state-head publication.");
        }

        var currentState = current.CurrentStateRevisionId
            ?? throw new InvalidDataException(
                $"World '{current.Id}' has no current state revision to fence.");
        var nextState = next.CurrentStateRevisionId
            ?? throw new InvalidDataException(
                $"World '{next.Id}' has no next state revision to publish.");
        if (nextState == currentState)
        {
            return;
        }

        if (current.CurrentEnvironmentRevisionId != next.CurrentEnvironmentRevisionId)
        {
            throw new InvalidDataException(
                $"World '{next.Id}' cannot combine an environment-head change with an active peer state commit.");
        }

        var revision = await _inner.LoadStateRevisionAsync(
            next.Id,
            nextState,
            cancellationToken)
            ?? throw new InvalidDataException(
                $"World '{next.Id}' cannot publish state revision '{nextState}' before its immutable revision metadata is stored.");
        if (revision.WorldId != next.Id ||
            revision.Id != nextState ||
            revision.ParentRevisionId != currentState ||
            revision.EnvironmentRevisionId != next.CurrentEnvironmentRevisionId ||
            !string.Equals(revision.AdapterId, next.GameAdapterId, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"World '{next.Id}' next state revision is not the exact direct child required for an active peer commit.");
        }

        _ = await _authorityFences.AdvanceActiveRevisionAsync(
            next.Id,
            _localUser,
            currentAuthority.Generation,
            currentState,
            nextState,
            cancellationToken);
    }

    private static bool SameUser(UserIdentity left, UserIdentity right)
        => string.Equals(left.Provider, right.Provider, StringComparison.OrdinalIgnoreCase) &&
           string.Equals(left.ExternalId, right.ExternalId, StringComparison.Ordinal);
}
