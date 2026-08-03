using SharedWorlds.Backend.Identity;
using SharedWorlds.Core.Domain;
using SharedWorlds.Core.Worlds;

namespace SharedWorlds.Backend.Worlds;

/// <summary>
/// Authenticated owner-private discovery boundary. Owner and target installation are derived only from
/// the validated Steward session; callers cannot submit either identity as catalog input.
/// </summary>
public sealed class OwnedPrivateWorldCatalogApplicationService
{
    private readonly IOwnedWorldLocationCatalogStore _store;
    private readonly OwnedPrivateWorldCatalogService _catalog = new();

    public OwnedPrivateWorldCatalogApplicationService(
        IOwnedWorldLocationCatalogStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        _store = store;
    }

    public async Task<IReadOnlyList<OwnedPrivateWorldCatalogEntry>> ListAsync(
        StewardAuthenticatedCaller caller,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(caller);
        var owner = new UserIdentity(
            caller.Identity.Subject.Provider,
            caller.Identity.Subject.ExternalId,
            caller.Identity.DisplayName);
        var claims = await _store.ListOwnerWorldLocationsAsync(
            owner.Provider,
            owner.ExternalId,
            OwnedPrivateWorldCatalogService.MaximumClaims,
            cancellationToken);
        return _catalog.Resolve(owner, caller.InstallationId, claims);
    }
}
