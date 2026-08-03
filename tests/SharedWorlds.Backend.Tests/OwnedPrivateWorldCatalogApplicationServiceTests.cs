using SharedWorlds.Backend.Identity;
using SharedWorlds.Backend.Worlds;
using SharedWorlds.Core.Domain;
using SharedWorlds.Core.Worlds;
using Xunit;

namespace SharedWorlds.Backend.Tests;

public sealed class OwnedPrivateWorldCatalogApplicationServiceTests
{
    [Fact]
    public async Task OwnerAndTargetInstallationComeOnlyFromAuthenticatedCaller()
    {
        var worldId = WorldId.New();
        var store = new CaptureStore
        {
            Claims =
            [
                new OwnedWorldLocationClaim(
                    worldId,
                    "steam",
                    "owner-1",
                    "pc-source",
                    RevisionId.New(),
                    RevisionId.New(),
                    DateTimeOffset.UtcNow)
                {
                    Presentation = new OwnedWorldPresentation("Factory World", "factorio")
                }
            ]
        };
        var service = new OwnedPrivateWorldCatalogApplicationService(store);
        var caller = new StewardAuthenticatedCaller(
            new VerifiedExternalIdentity(
                new ExternalIdentityRef("steam", "owner-1"),
                "Owner"),
            "pc-target",
            StewardSessionId.New());

        var entry = Assert.Single(await service.ListAsync(caller));

        Assert.Equal("steam", store.OwnerProvider);
        Assert.Equal("owner-1", store.OwnerExternalId);
        Assert.Equal(OwnedPrivateWorldCatalogService.MaximumClaims, store.MaximumClaims);
        Assert.Equal(BringHereAvailability.Available, entry.Availability);
        Assert.Equal("pc-source", entry.Source!.InstallationId);
    }

    private sealed class CaptureStore : IOwnedWorldLocationCatalogStore
    {
        public IReadOnlyList<OwnedWorldLocationClaim> Claims { get; init; } = [];
        public string? OwnerProvider { get; private set; }
        public string? OwnerExternalId { get; private set; }
        public int MaximumClaims { get; private set; }

        public Task<IReadOnlyList<OwnedWorldLocationClaim>> ListOwnerWorldLocationsAsync(
            string ownerProvider,
            string ownerExternalId,
            int maximumClaims,
            CancellationToken cancellationToken = default)
        {
            OwnerProvider = ownerProvider;
            OwnerExternalId = ownerExternalId;
            MaximumClaims = maximumClaims;
            return Task.FromResult(Claims);
        }
    }
}
