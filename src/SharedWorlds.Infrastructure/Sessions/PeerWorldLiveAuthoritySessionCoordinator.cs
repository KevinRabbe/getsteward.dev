using SharedWorlds.Core.Abstractions;
using SharedWorlds.Core.Domain;
using SharedWorlds.Core.Sessions;

namespace SharedWorlds.Infrastructure.Sessions;

/// <summary>
/// Serializes the peer host-handoff lifecycle with holder-controlled membership mutation. The wrapped
/// coordinator remains the authority implementation; this decorator only closes the process-local race
/// where Remove access and Request/Complete handoff could otherwise both act on the same member list.
/// </summary>
public sealed class PeerWorldLiveAuthoritySessionCoordinator : IWorldSessionCoordinator
{
    private readonly IWorldSessionCoordinator _inner;
    private readonly IWorldStorage _storage;
    private readonly PeerWorldLiveMemberRevocationRegistry _liveRevocations;
    private readonly PeerWorldLiveAuthorityMutationGate _mutations;

    public PeerWorldLiveAuthoritySessionCoordinator(
        IWorldSessionCoordinator inner,
        IWorldStorage storage,
        PeerWorldLiveMemberRevocationRegistry liveRevocations,
        PeerWorldLiveAuthorityMutationGate mutations)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(storage);
        ArgumentNullException.ThrowIfNull(liveRevocations);
        ArgumentNullException.ThrowIfNull(mutations);
        _inner = inner;
        _storage = storage;
        _liveRevocations = liveRevocations;
        _mutations = mutations;
    }

    public Task<WorldSession> GetSessionAsync(
        WorldId worldId,
        CancellationToken cancellationToken = default)
        => _inner.GetSessionAsync(worldId, cancellationToken);

    public Task<WorldSession> AcquireHostAsync(
        WorldId worldId,
        UserIdentity user,
        CancellationToken cancellationToken = default)
        => _inner.AcquireHostAsync(worldId, user, cancellationToken);

    public async Task RequestHandoffAsync(
        WorldId worldId,
        UserIdentity requestedHost,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(requestedHost);
        using var mutationLease = await _mutations.EnterAsync(cancellationToken);
        var world = await RequireCurrentSharedWorldAsync(worldId, cancellationToken);
        var generation = world.PeerAuthority!.Generation;
        _liveRevocations.ThrowIfRevoked(worldId, generation, requestedHost);

        await _inner.RequestHandoffAsync(
            worldId,
            requestedHost,
            cancellationToken);
    }

    public async Task CompleteHandoffAsync(
        WorldId worldId,
        UserIdentity newHost,
        RevisionId committedRevision,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(newHost);
        using var mutationLease = await _mutations.EnterAsync(cancellationToken);
        var world = await RequireCurrentSharedWorldAsync(worldId, cancellationToken);
        var generation = world.PeerAuthority!.Generation;
        _liveRevocations.ThrowIfRevoked(worldId, generation, newHost);
        using var liveCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _liveRevocations.GetCancellationToken(worldId, generation, newHost));

        await _inner.CompleteHandoffAsync(
            worldId,
            newHost,
            committedRevision,
            liveCancellation.Token);
    }

    public Task ReleaseHostAsync(
        WorldId worldId,
        UserIdentity user,
        CancellationToken cancellationToken = default)
        => _inner.ReleaseHostAsync(worldId, user, cancellationToken);

    private async Task<World> RequireCurrentSharedWorldAsync(
        WorldId worldId,
        CancellationToken cancellationToken)
    {
        var world = await _storage.LoadWorldAsync(worldId, cancellationToken)
            ?? throw new InvalidDataException(
                $"Cannot coordinate live peer authority for missing World '{worldId}'.");
        if (world.SharingMode != WorldSharingMode.Shared ||
            world.PeerAuthority is not { Generation: > 0 })
        {
            throw new InvalidOperationException(
                $"World '{worldId}' does not have persistent peer authority.");
        }

        return world;
    }
}
