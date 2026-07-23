using System.Security.Cryptography;
using System.Text;
using SharedWorlds.Backend.Identity;
using Xunit;

namespace SharedWorlds.Backend.Tests.Identity;

public sealed class StewardSessionServiceTests
{
    [Fact]
    public async Task CreateSessionIssuesShortAccessAndLongRefreshWithoutPersistingPlaintextTokens()
    {
        var clock = new TestClock();
        var store = new InMemorySessionStore();
        var tokens = new QueueTokenGenerator(
            accessTokens: ["access-one"],
            refreshTokens: ["refresh-one"]);
        var service = CreateService(store, clock, tokens);
        var identity = Steam("76561198000000001");

        var issued = await service.CreateSessionAsync(identity, "device-a");
        var caller = Assert.IsType<StewardAuthenticatedCaller>(
            await service.ValidateAccessTokenAsync(issued.AccessToken));

        Assert.Equal("access-one", issued.AccessToken);
        Assert.Equal("refresh-one", issued.RefreshToken);
        Assert.Equal(clock.Now + TimeSpan.FromMinutes(15), issued.AccessExpiresAt);
        Assert.Equal(clock.Now + TimeSpan.FromDays(30), issued.RefreshExpiresAt);
        Assert.Equal(identity, caller.Identity);
        Assert.Equal("device-a", caller.InstallationId);

        var session = Assert.Single(store.Sessions.Values);
        var access = Assert.Single(store.AccessCredentials.Values);
        Assert.Equal(Hash("refresh-one"), session.RefreshTokenHash);
        Assert.Equal(Hash("access-one"), access.AccessTokenHash);
        Assert.DoesNotContain("refresh-one", store.Sessions.Keys.Select(value => value.ToString()));
        Assert.DoesNotContain("access-one", store.AccessCredentials.Keys);
    }

    [Fact]
    public async Task ExpiredAccessCanBeRenewedByValidRefreshAndRefreshRotates()
    {
        var clock = new TestClock();
        var store = new InMemorySessionStore();
        var tokens = new QueueTokenGenerator(
            accessTokens: ["access-one", "access-two"],
            refreshTokens: ["refresh-one", "refresh-two"]);
        var service = CreateService(store, clock, tokens);
        await service.CreateSessionAsync(Steam("76561198000000001"), "device-a");

        clock.Advance(TimeSpan.FromMinutes(16));
        Assert.Null(await service.ValidateAccessTokenAsync("access-one"));

        var refreshed = await service.RefreshAsync("refresh-one", "device-a");
        var next = Assert.IsType<StewardSessionTokens>(refreshed.Tokens);

        Assert.Equal(RefreshStewardSessionStatus.Refreshed, refreshed.Status);
        Assert.Equal("access-two", next.AccessToken);
        Assert.Equal("refresh-two", next.RefreshToken);
        Assert.NotNull(await service.ValidateAccessTokenAsync("access-two"));

        var replay = await service.RefreshAsync("refresh-one", "device-a");
        Assert.Equal(RefreshStewardSessionStatus.InvalidCredential, replay.Status);
        Assert.Null(replay.Tokens);
    }

    [Fact]
    public async Task WrongInstallationCannotUseRefreshAndDoesNotConsumeValidCredential()
    {
        var clock = new TestClock();
        var store = new InMemorySessionStore();
        var tokens = new QueueTokenGenerator(
            accessTokens: ["access-one", "access-two"],
            refreshTokens: ["refresh-one", "refresh-two"]);
        var service = CreateService(store, clock, tokens);
        await service.CreateSessionAsync(Steam("76561198000000001"), "device-a");

        var wrongDevice = await service.RefreshAsync("refresh-one", "device-b");
        var correctDevice = await service.RefreshAsync("refresh-one", "device-a");

        Assert.Equal(RefreshStewardSessionStatus.InvalidCredential, wrongDevice.Status);
        Assert.Equal(RefreshStewardSessionStatus.Refreshed, correctDevice.Status);
    }

    [Fact]
    public async Task ReauthenticationOnSameInstallationInvalidatesPreviousIdentitySession()
    {
        var clock = new TestClock();
        var store = new InMemorySessionStore();
        var tokens = new QueueTokenGenerator(
            accessTokens: ["access-a", "access-b"],
            refreshTokens: ["refresh-a", "refresh-b"]);
        var service = CreateService(store, clock, tokens);

        await service.CreateSessionAsync(Steam("76561198000000001"), "same-device");
        await service.CreateSessionAsync(Steam("76561198000000002"), "same-device");

        Assert.Null(await service.ValidateAccessTokenAsync("access-a"));
        Assert.Equal(
            RefreshStewardSessionStatus.InvalidCredential,
            (await service.RefreshAsync("refresh-a", "same-device")).Status);

        var current = Assert.IsType<StewardAuthenticatedCaller>(
            await service.ValidateAccessTokenAsync("access-b"));
        Assert.Equal("76561198000000002", current.Identity.Subject.ExternalId);
    }

    [Fact]
    public async Task RevokingRefreshSessionInvalidatesAccessAndFutureRefresh()
    {
        var clock = new TestClock();
        var store = new InMemorySessionStore();
        var tokens = new QueueTokenGenerator(
            accessTokens: ["access-one"],
            refreshTokens: ["refresh-one"]);
        var service = CreateService(store, clock, tokens);
        await service.CreateSessionAsync(Steam("76561198000000001"), "device-a");

        var revoked = await service.RevokeAsync("refresh-one", "device-a");

        Assert.True(revoked);
        Assert.Null(await service.ValidateAccessTokenAsync("access-one"));
        Assert.Equal(
            RefreshStewardSessionStatus.InvalidCredential,
            (await service.RefreshAsync("refresh-one", "device-a")).Status);
    }

    [Fact]
    public async Task WrongInstallationCannotRevokeSession()
    {
        var clock = new TestClock();
        var store = new InMemorySessionStore();
        var tokens = new QueueTokenGenerator(
            accessTokens: ["access-one"],
            refreshTokens: ["refresh-one"]);
        var service = CreateService(store, clock, tokens);
        await service.CreateSessionAsync(Steam("76561198000000001"), "device-a");

        Assert.False(await service.RevokeAsync("refresh-one", "device-b"));
        Assert.NotNull(await service.ValidateAccessTokenAsync("access-one"));
    }

    [Fact]
    public async Task RefreshCredentialExpiresIndependentlyOfWorldAuthority()
    {
        var clock = new TestClock();
        var store = new InMemorySessionStore();
        var tokens = new QueueTokenGenerator(
            accessTokens: ["access-one"],
            refreshTokens: ["refresh-one"]);
        var service = CreateService(store, clock, tokens);
        await service.CreateSessionAsync(Steam("76561198000000001"), "device-a");

        clock.Advance(TimeSpan.FromDays(31));

        Assert.Null(await service.ValidateAccessTokenAsync("access-one"));
        Assert.Equal(
            RefreshStewardSessionStatus.InvalidCredential,
            (await service.RefreshAsync("refresh-one", "device-a")).Status);
    }

    [Fact]
    public async Task RefreshRotationExtendsSessionUsingConfiguredOperationalLifetime()
    {
        var clock = new TestClock();
        var store = new InMemorySessionStore();
        var tokens = new QueueTokenGenerator(
            accessTokens: ["access-one", "access-two"],
            refreshTokens: ["refresh-one", "refresh-two"]);
        var options = new StewardSessionOptions(TimeSpan.FromMinutes(5), TimeSpan.FromDays(7));
        var service = CreateService(store, clock, tokens, options);
        await service.CreateSessionAsync(Steam("76561198000000001"), "device-a");
        clock.Advance(TimeSpan.FromDays(1));

        var refreshed = await service.RefreshAsync("refresh-one", "device-a");
        var next = Assert.IsType<StewardSessionTokens>(refreshed.Tokens);

        Assert.Equal(clock.Now + TimeSpan.FromMinutes(5), next.AccessExpiresAt);
        Assert.Equal(clock.Now + TimeSpan.FromDays(7), next.RefreshExpiresAt);
    }

    private static StewardSessionService CreateService(
        InMemorySessionStore store,
        TestClock clock,
        QueueTokenGenerator tokens,
        StewardSessionOptions? options = null)
    {
        var nextSession = 0;
        return new StewardSessionService(
            store,
            () => clock.Now,
            options,
            tokens,
            () => new StewardSessionId(new Guid(++nextSession, 0, 0, new byte[8])));
    }

    private static VerifiedExternalIdentity Steam(string id)
        => new(new ExternalIdentityRef("steam", id));

    private static string Hash(string token)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    private sealed class TestClock
    {
        public DateTimeOffset Now { get; private set; } =
            new(2026, 7, 21, 22, 0, 0, TimeSpan.Zero);

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

    private sealed class InMemorySessionStore : IStewardSessionStore
    {
        private readonly object _gate = new();
        private readonly Dictionary<StewardSessionId, StewardSessionRecord> _sessions = [];
        private readonly Dictionary<string, StewardSessionId> _refreshIndex = new(StringComparer.Ordinal);
        private readonly Dictionary<string, StewardAccessCredentialRecord> _accessCredentials =
            new(StringComparer.Ordinal);

        public IReadOnlyDictionary<StewardSessionId, StewardSessionRecord> Sessions => _sessions;
        public IReadOnlyDictionary<string, StewardAccessCredentialRecord> AccessCredentials => _accessCredentials;

        public Task ReplaceInstallationSessionAsync(
            StewardSessionRecord session,
            StewardAccessCredentialRecord accessCredential,
            DateTimeOffset replacedAt,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_gate)
            {
                foreach (var existing in _sessions.Values
                             .Where(candidate =>
                                 !candidate.Revoked &&
                                 string.Equals(
                                     candidate.InstallationId,
                                     session.InstallationId,
                                     StringComparison.Ordinal))
                             .ToArray())
                {
                    _sessions[existing.Id] = existing with
                    {
                        Revoked = true,
                        RevokedAt = replacedAt
                    };
                    _refreshIndex.Remove(existing.RefreshTokenHash);
                }

                _sessions.Add(session.Id, session);
                _refreshIndex[session.RefreshTokenHash] = session.Id;
                _accessCredentials.Add(accessCredential.AccessTokenHash, accessCredential);
                return Task.CompletedTask;
            }
        }

        public Task<StewardAccessContext?> LoadAccessContextAsync(
            string accessTokenHash,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_gate)
            {
                if (!_accessCredentials.TryGetValue(accessTokenHash, out var access) ||
                    !_sessions.TryGetValue(access.SessionId, out var session))
                {
                    return Task.FromResult<StewardAccessContext?>(null);
                }

                return Task.FromResult<StewardAccessContext?>(new(session, access));
            }
        }

        public Task<StewardSessionRecord?> LoadRefreshSessionAsync(
            string refreshTokenHash,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_gate)
            {
                if (!_refreshIndex.TryGetValue(refreshTokenHash, out var sessionId) ||
                    !_sessions.TryGetValue(sessionId, out var session))
                {
                    return Task.FromResult<StewardSessionRecord?>(null);
                }

                return Task.FromResult<StewardSessionRecord?>(session);
            }
        }

        public Task<StoreRotateRefreshSessionStatus> TryRotateRefreshSessionAsync(
            StewardSessionId sessionId,
            string expectedRefreshTokenHash,
            string installationId,
            string newRefreshTokenHash,
            DateTimeOffset newRefreshExpiresAt,
            StewardAccessCredentialRecord newAccessCredential,
            DateTimeOffset rotatedAt,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_gate)
            {
                if (!_sessions.TryGetValue(sessionId, out var session) ||
                    session.Revoked ||
                    session.RefreshExpiresAt <= rotatedAt ||
                    !string.Equals(session.RefreshTokenHash, expectedRefreshTokenHash, StringComparison.Ordinal) ||
                    !string.Equals(session.InstallationId, installationId, StringComparison.Ordinal))
                {
                    return Task.FromResult(StoreRotateRefreshSessionStatus.InvalidCredential);
                }

                _refreshIndex.Remove(session.RefreshTokenHash);
                var updated = session with
                {
                    RefreshTokenHash = newRefreshTokenHash,
                    RefreshExpiresAt = newRefreshExpiresAt
                };
                _sessions[session.Id] = updated;
                _refreshIndex[newRefreshTokenHash] = session.Id;
                _accessCredentials.Add(newAccessCredential.AccessTokenHash, newAccessCredential);
                return Task.FromResult(StoreRotateRefreshSessionStatus.Rotated);
            }
        }

        public Task<bool> TryRevokeRefreshSessionAsync(
            string refreshTokenHash,
            string installationId,
            DateTimeOffset revokedAt,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_gate)
            {
                if (!_refreshIndex.TryGetValue(refreshTokenHash, out var sessionId) ||
                    !_sessions.TryGetValue(sessionId, out var session) ||
                    session.Revoked ||
                    !string.Equals(session.InstallationId, installationId, StringComparison.Ordinal))
                {
                    return Task.FromResult(false);
                }

                _sessions[sessionId] = session with
                {
                    Revoked = true,
                    RevokedAt = revokedAt
                };
                _refreshIndex.Remove(refreshTokenHash);
                return Task.FromResult(true);
            }
        }
    }
}
