using Npgsql;
using SharedWorlds.Backend.PostgreSql;
using SharedWorlds.Core.Domain;
using SharedWorlds.Core.Worlds;
using Xunit;

namespace SharedWorlds.Backend.PostgreSql.Tests;

public sealed class PostgreSqlOwnedWorldLocationCatalogStoreTests
{
    [Fact]
    public async Task CatalogReadIsOwnerScopedPresentationPreservingAndBounded()
    {
        var connectionString = Environment.GetEnvironmentVariable("STEWARD_TEST_POSTGRES");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return;
        }

        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        var mutationStore = new PostgreSqlOwnedWorldLocationStore(dataSource);
        await mutationStore.InitializeAsync();
        var catalogStore = new PostgreSqlOwnedWorldLocationCatalogStore(dataSource);
        var suffix = Guid.NewGuid().ToString("N");
        var owner = new UserIdentity("steam", $"owner-{suffix}", "Owner");
        var foreignOwner = new UserIdentity("steam", $"foreign-{suffix}", "Foreign");
        var registry = new OwnedWorldLocationRegistry(mutationStore);
        await registry.RegisterInstallationAsync(
            owner,
            $"pc-a-{suffix}",
            "PC A",
            DateTimeOffset.UtcNow);
        await registry.RegisterInstallationAsync(
            owner,
            $"pc-b-{suffix}",
            "PC B",
            DateTimeOffset.UtcNow);
        await registry.RegisterInstallationAsync(
            foreignOwner,
            $"pc-foreign-{suffix}",
            "Foreign PC",
            DateTimeOffset.UtcNow);

        var presentedWorld = WorldId.New();
        await registry.PublishLocationWithPresentationAsync(
            owner,
            $"pc-a-{suffix}",
            presentedWorld,
            RevisionId.New(),
            RevisionId.New(),
            "Factory World",
            "factorio",
            DateTimeOffset.UtcNow);
        var legacyWorld = WorldId.New();
        await registry.PublishLocationAsync(
            owner,
            $"pc-b-{suffix}",
            legacyWorld,
            RevisionId.New(),
            RevisionId.New(),
            DateTimeOffset.UtcNow);
        await registry.PublishLocationWithPresentationAsync(
            foreignOwner,
            $"pc-foreign-{suffix}",
            WorldId.New(),
            RevisionId.New(),
            RevisionId.New(),
            "Foreign World",
            "factorio",
            DateTimeOffset.UtcNow);

        var claims = await catalogStore.ListOwnerWorldLocationsAsync(
            owner.Provider,
            owner.ExternalId,
            OwnedPrivateWorldCatalogService.MaximumClaims);

        Assert.Equal(2, claims.Count);
        Assert.All(claims, claim => Assert.Equal(owner.ExternalId, claim.OwnerExternalId));
        Assert.Contains(claims, claim =>
            claim.WorldId == presentedWorld &&
            claim.Presentation == new OwnedWorldPresentation("Factory World", "factorio"));
        Assert.Contains(claims, claim =>
            claim.WorldId == legacyWorld && claim.Presentation is null);

        var bounded = await catalogStore.ListOwnerWorldLocationsAsync(
            owner.Provider,
            owner.ExternalId,
            maximumClaims: 1);
        Assert.Equal(2, bounded.Count);
    }
}
