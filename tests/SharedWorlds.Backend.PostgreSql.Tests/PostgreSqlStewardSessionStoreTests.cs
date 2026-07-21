using System.Security.Cryptography;
using System.Text;
using Npgsql;
using SharedWorlds.Backend.Identity;
using SharedWorlds.Backend.PostgreSql;
using Xunit;

namespace SharedWorlds.Backend.PostgreSql.Tests;

public sealed class PostgreSqlStewardSessionStoreTests : IAsyncLifetime
{
    private NpgsqlDataSource _dataSource = null!;
    private PostgreSqlStewardSessionStore _store = null!;

    public async Task InitializeAsync()
    {
        var connectionString = Environment.GetEnvironmentVariable("STEWARD_TEST_POSTGRES");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException(
                "STEWARD_TEST_POSTGRES must be set for PostgreSQL integration tests.");
        }

        _dataSource = NpgsqlDataSource.Create(connectionString);
        await PostgreSqlBackendSchema.InitializeAsync(_dataSource);
        _store = new PostgreSqlStewardSessionStore(_dataSource);
    }

    public async Task DisposeAsync()
    {
        await _dataSource.DisposeAsync();
    }

    [Fact]
    public async Task SessionPersistsOnlyTokenHashesAndValidatesThroughService()
    {
        await ResetAsync();
        var clock = new TestClock();
        var service = CreateService(
            clock,
            accessTokens: ["access-secret"],
            refreshTokens: ["refresh-secret"]);

        var issued = await service.CreateSessionAsync(
            Steam("76561198000000001"),
            "device-a");
        var caller = Assert.IsType<StewardAuthenticatedCaller>(
            await service.ValidateAccessTokenAsync(issued.AccessToken));

        Assert.Equal("76561198000000001", caller.Identity.Subject.ExternalId);
        Assert.Equal("device-a", caller.InstallationId);

        await using var command = _dataSource.CreateCommand(
            """
            SELECT s.refresh_token_hash, a.access_token_hash
            FROM steward_auth_sessions s
            INNER JOIN steward_access_credentials a ON a.session_id = s.session_id;
            """);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(Hash("refresh-secret"), reader.GetString(0));
        Assert.Equal(Hash("access-secret"), reader.GetString(1));
        Assert.NotEqual("refresh-secret", reader.GetString(0));
        Assert.NotEqual("access-secret", reader.GetString(1));
    }

    [Fact]
    public async Task RefreshRotatesCredentialAndWrongInstallationCannotConsumeIt()
    {
        await ResetAsync();
        var clock = new TestClock();
        var service = CreateService(
            clock,
            accessTokens: ["access-one", "access-two"],
            refreshTokens: ["refresh-one", "refresh-two"]);
        await service.CreateSessionAsync(Steam("76561198000000001"), "device-a");

        Assert.Equal(
            RefreshStewardSessionStatus.InvalidCredential,
            (await service.RefreshAsync("refresh-one", "device-b")).Status);

        var refreshed = await service.RefreshAsync("refresh-one", "device-a");
        Assert.Equal(RefreshStewardSessionStatus.Refreshed, refreshed.Status);
        Assert.NotNull(await service.ValidateAccessTokenAsync("access-two"));
        Assert.Equal(
            RefreshStewardSessionStatus.InvalidCredential,
            (await service.RefreshAsync("refresh-one", "device-a")).Status);
        Assert.Equal(
            RefreshStewardSessionStatus.Refreshed,
            (await service.RefreshAsync("refresh-two", "device-a")).Status);
    }

    [Fact]
    public async Task ReauthenticationOnSameInstallationRevokesPreviousIdentitySession()
    {
        await ResetAsync();
        var clock = new TestClock();
        var service = CreateService(
            clock,
            accessTokens: ["access-a", "access-b"],
            refreshTokens: ["refresh-a", "refresh-b"]);

        await service.CreateSessionAsync(Steam("76561198000000001"), "same-device");
        await service.CreateSessionAsync(Steam("76561198000000002"), "same-device");

        Assert.Null(await service.ValidateAccessTokenAsync("access-a"));
        Assert.Equal(
            RefreshStewardSessionStatus.InvalidCredential,
            (await service.RefreshAsync("refresh-a", "same-device")).Status);

        var current = Assert.IsType<StewardAuthenticatedCaller>(
            await service.ValidateAccessTokenAsync("access-b"));
        Assert.Equal("76561198000000002", current.Identity.Subject.ExternalId);

        await using var countCommand = _dataSource.CreateCommand(
            "SELECT count(*) FROM steward_auth_sessions WHERE installation_id = 'same-device' AND revoked = false;");
        Assert.Equal(1L, (long)(await countCommand.ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task RevokeInvalidatesAccessAndRefreshInPersistentStore()
    {
        await ResetAsync();
        var clock = new TestClock();
        var service = CreateService(
            clock,
            accessTokens: ["access-one"],
            refreshTokens: ["refresh-one"]);
        await service.CreateSessionAsync(Steam("76561198000000001"), "device-a");

        Assert.True(await service.RevokeAsync("refresh-one", "device-a"));
        Assert.Null(await service.ValidateAccessTokenAsync("access-one"));
        Assert.Equal(
            RefreshStewardSessionStatus.InvalidCredential,
            (await service.RefreshAsync("refresh-one", "device-a")).Status);
    }

    [Fact]
    public async Task ExpiredRefreshCannotRotateEvenWhenRowStillExistsForAudit()
    {
        await ResetAsync();
        var clock = new TestClock();
        var service = CreateService(
            clock,
            accessTokens: ["access-one"],
            refreshTokens: ["refresh-one"]);
        await service.CreateSessionAsync(Steam("76561198000000001"), "device-a");
        clock.Advance(TimeSpan.FromDays(31));

        Assert.Equal(
            RefreshStewardSessionStatus.InvalidCredential,
            (await service.RefreshAsync("refresh-one", "device-a")).Status);

        await using var command = _dataSource.CreateCommand(
            "SELECT count(*) FROM steward_auth_sessions WHERE refresh_token_hash = @hash;");
        command.Parameters.AddWithValue("hash", Hash("refresh-one"));
        Assert.Equal(1L, (long)(await command.ExecuteScalarAsync())!);
    }

    private StewardSessionService CreateService(
        TestClock clock,
        IEnumerable<string> accessTokens,
        IEnumerable<string> refreshTokens)
    {
        var nextSession = 0;
        return new StewardSessionService(
            _store,
            () => clock.Now,
            StewardSessionOptions.FirstReleaseDefaults,
            new QueueTokenGenerator(accessTokens, refreshTokens),
            () => new StewardSessionId(new Guid(++nextSession, 0, 0, new byte[8])));
    }

    private async Task ResetAsync()
    {
        await using var command = _dataSource.CreateCommand(
            "TRUNCATE TABLE steward_access_credentials, steward_auth_sessions CASCADE;");
        await command.ExecuteNonQueryAsync();
    }

    private static VerifiedExternalIdentity Steam(string id)
        => new(new ExternalIdentityRef("steam", id));

    private static string Hash(string token)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    private sealed class TestClock
    {
        public DateTimeOffset Now { get; private set; } =
            new(2026, 7, 22, 1, 0, 0, TimeSpan.Zero);

        public void Advance(TimeSpan duration) => Now += duration;
    }

    private sealed class QueueTokenGenerator : IStewardSessionTokenGenerator
    {
        private readonly Queue<string> _accessTokens;
        private readonly Queue<string> _refreshTokens;

        public QueueTokenGenerator(
            IEnumerable<string> accessTokens,
            IEnumerable<string> refreshTokens)
        {
            _accessTokens = new Queue<string>(accessTokens);
            _refreshTokens = new Queue<string>(refreshTokens);
        }

        public string CreateAccessToken() => _accessTokens.Dequeue();
        public string CreateRefreshToken() => _refreshTokens.Dequeue();
    }
}
