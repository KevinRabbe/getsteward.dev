using Npgsql;
using SharedWorlds.Backend.PostgreSql;
using SharedWorlds.Core.Domain;
using SharedWorlds.Core.Worlds;
using Xunit;

namespace SharedWorlds.Backend.PostgreSql.Tests;

public sealed class PostgreSqlOwnedWorldLocationStoreTests
{
    [Fact]
    public async Task RegistrationAndLocationCompareExchangeRemainOwnerScoped()
    {
        var connectionString = Environment.GetEnvironmentVariable("STEWARD_TEST_POSTGRES");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return;
        }

        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        var store = new PostgreSqlOwnedWorldLocationStore(dataSource);
        await store.InitializeAsync();

        var suffix = Guid.NewGuid().ToString("N");
        var owner = new UserIdentity("steam", $"owner-{suffix}", "Owner");
        var otherOwner = new UserIdentity("steam", $"other-{suffix}", "Other");
        var installationId = $"pc-{suffix}";
        var registry = new OwnedWorldLocationRegistry(store);
        var observedAt = DateTimeOffset.UtcNow;

        await registry.RegisterInstallationAsync(
            owner,
            installationId,
            "Gaming PC",
            observedAt);

        var reassignment = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            registry.RegisterInstallationAsync(
                otherOwner,
                installationId,
                "Other PC",
                observedAt.AddMinutes(1)));
        Assert.Contains("different authenticated owner", reassignment.Message, StringComparison.Ordinal);

        var persistedInstallation = await store.GetInstallationAsync(installationId);
        Assert.NotNull(persistedInstallation);
        Assert.Equal(owner.ExternalId, persistedInstallation.OwnerExternalId);

        var worldId = WorldId.New();
        var firstState = RevisionId.New();
        var firstEnvironment = RevisionId.New();
        var created = await registry.PublishLocationAsync(
            owner,
            installationId,
            worldId,
            firstState,
            firstEnvironment,
            observedAt);
        Assert.Equal(OwnedWorldLocationWriteResult.Created, created.Result);

        var nextState = RevisionId.New();
        var nextEnvironment = RevisionId.New();
        var conflict = await registry.PublishLocationAsync(
            owner,
            installationId,
            worldId,
            nextState,
            nextEnvironment,
            observedAt.AddYears(10),
            expectedStateRevisionId: RevisionId.New(),
            expectedEnvironmentRevisionId: firstEnvironment);
        Assert.Equal(OwnedWorldLocationWriteResult.Conflict, conflict.Result);
        Assert.Equal(firstState, conflict.Current?.StateRevisionId);

        var afterConflict = Assert.Single(await store.ListWorldLocationsAsync(
            worldId,
            owner.Provider,
            owner.ExternalId));
        Assert.Equal(firstState, afterConflict.StateRevisionId);
        Assert.Equal(firstEnvironment, afterConflict.EnvironmentRevisionId);

        var updated = await registry.PublishLocationAsync(
            owner,
            installationId,
            worldId,
            nextState,
            nextEnvironment,
            observedAt.AddMinutes(2),
            expectedStateRevisionId: firstState,
            expectedEnvironmentRevisionId: firstEnvironment);
        Assert.Equal(OwnedWorldLocationWriteResult.Updated, updated.Result);

        var repeated = await registry.PublishLocationAsync(
            owner,
            installationId,
            worldId,
            nextState,
            nextEnvironment,
            observedAt.AddMinutes(3));
        Assert.Equal(OwnedWorldLocationWriteResult.NoChange, repeated.Result);

        var removedConflict = await registry.RemoveLocationAsync(
            owner,
            installationId,
            worldId,
            firstState,
            firstEnvironment);
        Assert.Equal(OwnedWorldLocationWriteResult.Conflict, removedConflict.Result);

        var removed = await registry.RemoveLocationAsync(
            owner,
            installationId,
            worldId,
            nextState,
            nextEnvironment);
        Assert.Equal(OwnedWorldLocationWriteResult.Updated, removed.Result);
        Assert.Empty(await store.ListWorldLocationsAsync(
            worldId,
            owner.Provider,
            owner.ExternalId));
    }

    [Fact]
    public async Task StoreRejectsLocationForInstallationOwnedByAnotherIdentity()
    {
        var connectionString = Environment.GetEnvironmentVariable("STEWARD_TEST_POSTGRES");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return;
        }

        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        var store = new PostgreSqlOwnedWorldLocationStore(dataSource);
        await store.InitializeAsync();

        var suffix = Guid.NewGuid().ToString("N");
        var installationId = $"pc-{suffix}";
        var owner = new UserIdentity("steam", $"owner-{suffix}", "Owner");
        var registry = new OwnedWorldLocationRegistry(store);
        await registry.RegisterInstallationAsync(
            owner,
            installationId,
            "Gaming PC",
            DateTimeOffset.UtcNow);

        var desired = new OwnedWorldLocationClaim(
            WorldId.New(),
            "steam",
            $"attacker-{suffix}",
            installationId,
            RevisionId.New(),
            RevisionId.New(),
            DateTimeOffset.UtcNow);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.CompareExchangeLocationAsync(
                desired,
                expectedStateRevisionId: null,
                expectedEnvironmentRevisionId: null));
        Assert.Contains("different authenticated owner", exception.Message, StringComparison.Ordinal);
    }
}
