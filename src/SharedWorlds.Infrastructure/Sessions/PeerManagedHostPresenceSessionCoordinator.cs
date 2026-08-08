using SharedWorlds.Core.Abstractions;
using SharedWorlds.Core.Domain;
using SharedWorlds.Core.Errors;
using SharedWorlds.Core.Sessions;

namespace SharedWorlds.Infrastructure.Sessions;

/// <summary>
/// Adds process-local managed-host joinability evidence around an already authoritative peer session
/// coordinator. Writable authority, generation changes, revision handoff, and lobby ownership remain
/// entirely owned by the inner coordinator. This decorator publishes only the adapter-owned local game
/// endpoint needed by a later authenticated peer game transport.
/// </summary>
public sealed class PeerManagedHostPresenceSessionCoordinator : IWorldSessionCoordinator
{
    private readonly IWorldSessionCoordinator _inner;
    private readonly IWorldStorage _storage;
    private readonly IPeerManagedHostPresenceRegistry _presence;
    private readonly UserIdentity _localUser;

    public PeerManagedHostPresenceSessionCoordinator(
        IWorldSessionCoordinator inner,
        IWorldStorage storage,
        IPeerManagedHostPresenceRegistry presence,
        UserIdentity localUser)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(storage);
        ArgumentNullException.ThrowIfNull(presence);
        ArgumentNullException.ThrowIfNull(localUser);
        _inner = inner;
        _storage = storage;
        _presence = presence;
        _localUser = localUser;
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

    public async Task MarkHostStartingAsync(
        WorldId worldId,
        CancellationToken cancellationToken = default)
    {
        var generation = await RequireCurrentLocalHostingGenerationAsync(
            worldId,
            cancellationToken);
        _ = await _presence.MarkStartingAsync(
            worldId,
            _localUser,
            generation,
            cancellationToken);
    }

    public async Task MarkHostReadyAsync(
        WorldId worldId,
        ManagedHostEndpoint endpoint,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        var generation = await RequireCurrentLocalHostingGenerationAsync(
            worldId,
            cancellationToken);
        _ = await _presence.MarkReadyAsync(
            worldId,
            _localUser,
            generation,
            endpoint,
            cancellationToken);
    }

    public async Task EndHostPresenceAsync(
        WorldId worldId,
        CancellationToken cancellationToken = default)
    {
        var current = await _presence.GetAsync(worldId, cancellationToken);
        if (current is null || !SameUser(current.Holder, _localUser))
        {
            return;
        }

        await _presence.EndAsync(
            worldId,
            _localUser,
            current.AuthorityGeneration,
            cancellationToken);
    }

    public Task RequestHandoffAsync(
        WorldId worldId,
        UserIdentity requestedHost,
        CancellationToken cancellationToken = default)
        => _inner.RequestHandoffAsync(worldId, requestedHost, cancellationToken);

    public Task CompleteHandoffAsync(
        WorldId worldId,
        UserIdentity newHost,
        RevisionId committedRevision,
        CancellationToken cancellationToken = default)
        => _inner.CompleteHandoffAsync(
            worldId,
            newHost,
            committedRevision,
            cancellationToken);

    public Task ReleaseHostAsync(
        WorldId worldId,
        UserIdentity user,
        CancellationToken cancellationToken = default)
        => _inner.ReleaseHostAsync(worldId, user, cancellationToken);

    private async Task<ulong> RequireCurrentLocalHostingGenerationAsync(
        WorldId worldId,
        CancellationToken cancellationToken)
    {
        var session = await _inner.GetSessionAsync(worldId, cancellationToken);
        if (session.State != SessionState.Hosting ||
            session.ActiveHost is null ||
            !SameUser(session.ActiveHost, _localUser))
        {
            throw new WorldSessionConflictException(
                worldId,
                "Managed host presence can be published only by the currently confirmed local peer host.");
        }

        var world = await _storage.LoadWorldAsync(worldId, cancellationToken)
            ?? throw new WorldSessionConflictException(
                worldId,
                "The local canonical World metadata is missing while publishing managed host presence.");
        if (world.SharingMode != WorldSharingMode.Shared ||
            world.PeerAuthority is not { } authority ||
            authority.Generation == 0 ||
            !SameUser(authority.Holder, _localUser))
        {
            throw new WorldSessionConflictException(
                worldId,
                "Local persistent peer authority does not match the confirmed managed host.");
        }

        return authority.Generation;
    }

    private static bool SameUser(UserIdentity left, UserIdentity right)
        => string.Equals(left.Provider, right.Provider, StringComparison.OrdinalIgnoreCase) &&
           string.Equals(left.ExternalId, right.ExternalId, StringComparison.Ordinal);
}
