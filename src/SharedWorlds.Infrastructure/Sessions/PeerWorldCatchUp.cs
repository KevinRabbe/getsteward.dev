using SharedWorlds.Core.Abstractions;
using SharedWorlds.Core.Domain;

namespace SharedWorlds.Infrastructure.Sessions;

public sealed record PeerWorldCatchUpRequest(
    WorldId WorldId,
    ulong AuthorityGeneration,
    RevisionId? LocalStateRevisionId);

public sealed record PeerWorldCatchUpResult(
    WorldId WorldId,
    ulong AuthorityGeneration,
    RevisionId CurrentStateRevisionId);

/// <summary>
/// Client-side control boundary for asking the confirmed active host to bring this participant's local
/// replica to the current canonical head. Transport authenticates <paramref name="confirmedHost"/>;
/// request payload never supplies or overrides peer identity.
/// </summary>
public interface IPeerWorldCatchUpRequestClient
{
    Task<PeerWorldCatchUpResult> RequestCatchUpAsync(
        UserIdentity confirmedHost,
        PeerWorldCatchUpRequest request,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Host-side authority gate for first-time bootstrap and returning-observer synchronization requests.
/// It is deliberately transport-independent: the Steam engine supplies the already-authenticated
/// remote identity, while this router proves canonical World authority, durable Active authority,
/// live lobby authority/generation, and canonical membership before invoking any transfer service.
/// </summary>
public sealed class PeerWorldCatchUpRequestRouter
{
    private readonly IWorldStorage _storage;
    private readonly IPeerWorldLobby _lobby;
    private readonly IPeerAuthorityFenceStore _authorityFences;
    private readonly UserIdentity _localUser;
    private readonly object _bindingGate = new();
    private PeerWorldBootstrapTransferService? _bootstrap;
    private PeerWorldObserverSyncService? _observerSync;

    public PeerWorldCatchUpRequestRouter(
        IWorldStorage storage,
        IPeerWorldLobby lobby,
        IPeerAuthorityFenceStore authorityFences,
        UserIdentity localUser)
    {
        ArgumentNullException.ThrowIfNull(storage);
        ArgumentNullException.ThrowIfNull(lobby);
        ArgumentNullException.ThrowIfNull(authorityFences);
        ArgumentNullException.ThrowIfNull(localUser);
        _storage = storage;
        _lobby = lobby;
        _authorityFences = authorityFences;
        _localUser = localUser;
    }

    /// <summary>
    /// Resolves the construction cycle exactly once. The unified Steam exchange must exist before the
    /// source transfer services can be constructed, while incoming catch-up requests need those source
    /// services. Binding after composition keeps one transport engine without service-locator fallback.
    /// </summary>
    public void Bind(
        PeerWorldBootstrapTransferService bootstrap,
        PeerWorldObserverSyncService observerSync)
    {
        ArgumentNullException.ThrowIfNull(bootstrap);
        ArgumentNullException.ThrowIfNull(observerSync);
        lock (_bindingGate)
        {
            if (_bootstrap is not null || _observerSync is not null)
            {
                throw new InvalidOperationException(
                    "Peer World catch-up request router is already bound.");
            }

            _bootstrap = bootstrap;
            _observerSync = observerSync;
        }
    }

    public async Task<PeerWorldCatchUpResult> HandleAsync(
        UserIdentity authenticatedRemoteUser,
        PeerWorldCatchUpRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(authenticatedRemoteUser);
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        if (request.WorldId.Value == Guid.Empty)
        {
            throw new ArgumentException("Catch-up request requires a World ID.", nameof(request));
        }

        if (request.AuthorityGeneration == 0)
        {
            throw new InvalidDataException(
                "Peer World catch-up request requires a nonzero authority generation.");
        }

        if (request.LocalStateRevisionId is { } localRevision &&
            localRevision.Value == Guid.Empty)
        {
            throw new InvalidDataException(
                "Peer World catch-up request contains an empty local state revision ID.");
        }

        if (SameUser(authenticatedRemoteUser, _localUser))
        {
            throw new InvalidOperationException(
                "The active host cannot request observer catch-up from itself.");
        }

        var services = RequireBoundServices();
        var before = await RequireCurrentAuthorityAsync(
            authenticatedRemoteUser,
            request,
            cancellationToken);
        var currentStateRevisionId = before.CurrentStateRevisionId!.Value;

        if (request.LocalStateRevisionId is null)
        {
            await services.Bootstrap.BootstrapAsync(
                request.WorldId,
                authenticatedRemoteUser,
                cancellationToken);
        }
        else if (request.LocalStateRevisionId.Value != currentStateRevisionId)
        {
            await services.ObserverSync.SynchronizeAsync(
                request.WorldId,
                authenticatedRemoteUser,
                request.LocalStateRevisionId.Value,
                cancellationToken);
        }

        // A transfer may take long enough for gameplay shutdown/handoff to start. Never report a
        // successful current-head result unless the same holder/generation/head are still canonical
        // and the durable/lobby authority views still agree after transfer completion.
        var after = await RequireCurrentAuthorityAsync(
            authenticatedRemoteUser,
            request,
            cancellationToken);
        if (after.CurrentStateRevisionId != currentStateRevisionId)
        {
            throw new InvalidOperationException(
                "The canonical World head changed while peer catch-up was running. Retry against the new host state.");
        }

        return new PeerWorldCatchUpResult(
            request.WorldId,
            request.AuthorityGeneration,
            currentStateRevisionId);
    }

    private async Task<World> RequireCurrentAuthorityAsync(
        UserIdentity authenticatedRemoteUser,
        PeerWorldCatchUpRequest request,
        CancellationToken cancellationToken)
    {
        var world = await _storage.LoadWorldAsync(
            request.WorldId,
            cancellationToken)
            ?? throw new InvalidDataException(
                $"Cannot serve catch-up for missing canonical World '{request.WorldId}'.");
        if (world.SharingMode != WorldSharingMode.Shared)
        {
            throw new InvalidOperationException(
                $"World '{request.WorldId}' is local-only and has no peer catch-up path.");
        }

        var authority = world.PeerAuthority
            ?? throw new InvalidDataException(
                $"World '{request.WorldId}' has no persistent peer authority.");
        var currentStateRevisionId = world.CurrentStateRevisionId
            ?? throw new InvalidDataException(
                $"World '{request.WorldId}' has no canonical state revision.");
        if (authority.Generation == 0 ||
            authority.Generation != request.AuthorityGeneration ||
            !SameUser(authority.Holder, _localUser) ||
            !ContainsStableMember(world.Members, _localUser) ||
            !ContainsStableMember(world.Members, authenticatedRemoteUser))
        {
            throw new InvalidOperationException(
                "Peer catch-up request does not match the current local authority generation or canonical membership.");
        }

        var fence = await _authorityFences.LoadAsync(
            request.WorldId,
            cancellationToken)
            ?? throw new InvalidOperationException(
                "The active host has no durable peer-authority fence for this World.");
        if (fence.State != PeerAuthorityFenceState.Active ||
            fence.Generation != authority.Generation ||
            fence.StateRevisionId != currentStateRevisionId ||
            !SameUser(fence.Holder, _localUser))
        {
            throw new InvalidOperationException(
                "The active host's durable authority fence does not match the requested World head and generation.");
        }

        var lobby = await _lobby.GetAsync(request.WorldId, cancellationToken)
            ?? throw new InvalidOperationException(
                "The requested World has no active peer lobby on this host.");
        if (!lobby.OwnerConfirmed ||
            lobby.AuthorityGeneration != authority.Generation ||
            !SameUser(lobby.Owner, _localUser) ||
            lobby.RequestedHost is not null)
        {
            throw new InvalidOperationException(
                "The live peer lobby does not confirm this host/generation or a host handoff is in progress.");
        }

        return world;
    }

    private (PeerWorldBootstrapTransferService Bootstrap, PeerWorldObserverSyncService ObserverSync)
        RequireBoundServices()
    {
        lock (_bindingGate)
        {
            return _bootstrap is not null && _observerSync is not null
                ? (_bootstrap, _observerSync)
                : throw new InvalidOperationException(
                    "Peer World catch-up request router is not bound to the unified transfer services.");
        }
    }

    private static bool ContainsStableMember(
        IReadOnlyList<UserIdentity> members,
        UserIdentity expected)
        => members.Any(member => SameUser(member, expected));

    private static bool SameUser(UserIdentity left, UserIdentity right)
        => string.Equals(left.Provider, right.Provider, StringComparison.OrdinalIgnoreCase) &&
           string.Equals(left.ExternalId, right.ExternalId, StringComparison.Ordinal);
}
