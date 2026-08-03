using Npgsql;
using SharedWorlds.Backend.PostgreSql;
using SharedWorlds.Core.Domain;
using SharedWorlds.Core.Worlds;

namespace SharedWorlds.Backend.PostgreSql.Tests;

public sealed class PostgreSqlOwnedWorldLocationConcurrencyTests
{
    [Fact]
    public async Task ConcurrentFirstPublicationNeverCreatesTwoDifferentClaims()
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
        var installationId = $"pc-{suffix}";
        var registry = new OwnedWorldLocationRegistry(store);
        await registry.RegisterInstallationAsync(
            owner,
            installationId,
            "Gaming PC",
            DateTimeOffset.UtcNow);

        var worldId = WorldId.New();
        var first = registry.PublishLocationAsync(
            owner,
            installationId,
            worldId,
            RevisionId.New(),
            RevisionId.New(),
            DateTimeOffset.UtcNow);
        var second = registry.PublishLocationAsync(
            owner,
            installationId,
            worldId,
            RevisionId.New(),
            RevisionId.New(),
            DateTimeOffset.UtcNow.AddMilliseconds(1));

        OwnedWorldLocationWriteDecision[] results;
        try
        {
            results = await Task.WhenAll(first, second);
        }
        catch (PostgresException exception)
        {
            throw new Xunit.Sdk.XunitException(
                $"Concurrent publication leaked PostgreSQL error {exception.SqlState}: {exception.MessageText}");
        }

        Assert.Single(results.Where(result => result.Result == OwnedWorldLocationWriteResult.Created));
        Assert.Single(results.Where(result => result.Result == OwnedWorldLocationWriteResult.Conflict));
        Assert.Single(await store.ListWorldLocationsAsync(
            worldId,
            owner.Provider,
            owner.ExternalId));
    }
}
