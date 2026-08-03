using SharedWorlds.Backend.Identity;
using SharedWorlds.Core.Domain;
using SharedWorlds.Core.Worlds;

namespace SharedWorlds.Backend.Worlds;

/// <summary>
/// Authenticated backend application boundary for private owned-device World locations.
/// Caller identity and installation identity come only from a validated Steward session.
/// </summary>
public sealed class OwnedWorldLocationApplicationService
{
    private readonly IOwnedWorldLocationStore _store;
    private readonly OwnedWorldLocationRegistry _registry;
    private readonly BringHereAuthorityService _authority;
    private readonly Func<DateTimeOffset> _utcNow;

    public OwnedWorldLocationApplicationService(
        IOwnedWorldLocationStore store,
        Func<DateTimeOffset> utcNow)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(utcNow);
        _store = store;
        _registry = new OwnedWorldLocationRegistry(store);
        _authority = new BringHereAuthorityService();
        _utcNow = utcNow;
    }

    public Task<OwnedInstallationRegistration> RegisterCurrentInstallationAsync(
        StewardAuthenticatedCaller caller,
        string displayName,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(caller);
        return _registry.RegisterInstallationAsync(
            ToUserIdentity(caller.Identity),
            caller.InstallationId,
            displayName,
            _utcNow(),
            cancellationToken);
    }

    public Task<OwnedWorldLocationWriteDecision> PublishCurrentLocationAsync(
        StewardAuthenticatedCaller caller,
        WorldId worldId,
        RevisionId stateRevisionId,
        RevisionId environmentRevisionId,
        RevisionId? expectedStateRevisionId = null,
        RevisionId? expectedEnvironmentRevisionId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(caller);
        return _registry.PublishLocationAsync(
            ToUserIdentity(caller.Identity),
            caller.InstallationId,
            worldId,
            stateRevisionId,
            environmentRevisionId,
            _utcNow(),
            expectedStateRevisionId,
            expectedEnvironmentRevisionId,
            cancellationToken);
    }

    public Task<OwnedWorldLocationWriteDecision> PublishCurrentLocationWithPresentationAsync(
        StewardAuthenticatedCaller caller,
        WorldId worldId,
        RevisionId stateRevisionId,
        RevisionId environmentRevisionId,
        string worldName,
        string gameAdapterId,
        RevisionId? expectedStateRevisionId = null,
        RevisionId? expectedEnvironmentRevisionId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(caller);
        return _registry.PublishLocationWithPresentationAsync(
            ToUserIdentity(caller.Identity),
            caller.InstallationId,
            worldId,
            stateRevisionId,
            environmentRevisionId,
            worldName,
            gameAdapterId,
            _utcNow(),
            expectedStateRevisionId,
            expectedEnvironmentRevisionId,
            cancellationToken);
    }

    public Task<OwnedWorldLocationWriteDecision> RemoveCurrentLocationAsync(
        StewardAuthenticatedCaller caller,
        WorldId worldId,
        RevisionId expectedStateRevisionId,
        RevisionId expectedEnvironmentRevisionId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(caller);
        return _registry.RemoveLocationAsync(
            ToUserIdentity(caller.Identity),
            caller.InstallationId,
            worldId,
            expectedStateRevisionId,
            expectedEnvironmentRevisionId,
            cancellationToken);
    }

    public async Task<BringHereAuthorityDecision> ResolveBringHereAsync(
        StewardAuthenticatedCaller caller,
        WorldId worldId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(caller);
        var owner = ToUserIdentity(caller.Identity);
        var claims = await _store.ListWorldLocationsAsync(
            worldId,
            owner.Provider,
            owner.ExternalId,
            cancellationToken);
        return _authority.Resolve(
            worldId,
            owner,
            caller.InstallationId,
            claims);
    }

    private static UserIdentity ToUserIdentity(VerifiedExternalIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        return new UserIdentity(
            identity.Subject.Provider,
            identity.Subject.ExternalId,
            identity.DisplayName);
    }
}
