using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.TestHost;
using SharedWorlds.Backend.Api;
using SharedWorlds.Backend.Identity;
using SharedWorlds.Backend.Worlds;
using SharedWorlds.Core.Domain;
using Xunit;

namespace SharedWorlds.Backend.Api.Tests;

public sealed class StewardAuthorityReservationIdempotencyApiTests
{
    [Fact]
    public async Task AcquireRequiresIdempotencyKey()
    {
        await using var harness = await AuthorityHarness.CreateAsync();

        using var response = await harness.Client.PostAsJsonAsync(
            harness.AcquireUri,
            harness.AcquireRequest);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("IdempotencyKeyRequired", body.RootElement.GetProperty("code").GetString());
        Assert.Equal(0, harness.Authority.AcquireExecutionCount);
    }

    [Fact]
    public async Task AcquireRetryWithSameKeyReturnsOriginalReservationWithoutReexecution()
    {
        await using var harness = await AuthorityHarness.CreateAsync();
        const string key = "acquire-api-retry-1";

        using var firstRequest = CreateJsonRequest(
            harness.AcquireUri,
            key,
            harness.AcquireRequest);
        using var first = await harness.Client.SendAsync(firstRequest);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var firstText = await first.Content.ReadAsStringAsync();

        using var retryRequest = CreateJsonRequest(
            harness.AcquireUri,
            key,
            harness.AcquireRequest);
        using var retry = await harness.Client.SendAsync(retryRequest);
        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
        var retryText = await retry.Content.ReadAsStringAsync();

        Assert.Equal(firstText, retryText);
        using var body = JsonDocument.Parse(retryText);
        Assert.Equal("ReservationAcquired", body.RootElement.GetProperty("code").GetString());
        Assert.Equal(1, harness.Authority.AcquireExecutionCount);
    }

    [Fact]
    public async Task AcquireKeyReuseWithDifferentRequestReturnsStableConflict()
    {
        await using var harness = await AuthorityHarness.CreateAsync();
        const string key = "acquire-api-conflict-1";

        using var firstRequest = CreateJsonRequest(
            harness.AcquireUri,
            key,
            harness.AcquireRequest);
        using var first = await harness.Client.SendAsync(firstRequest);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        var changed = harness.AcquireRequest with { InstallationId = "device-b" };
        using var conflictingRequest = CreateJsonRequest(harness.AcquireUri, key, changed);
        using var conflict = await harness.Client.SendAsync(conflictingRequest);

        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        using var body = JsonDocument.Parse(await conflict.Content.ReadAsStringAsync());
        Assert.Equal("IdempotencyKeyConflict", body.RootElement.GetProperty("code").GetString());
        Assert.Equal(1, harness.Authority.AcquireExecutionCount);
    }

    [Fact]
    public async Task ReclaimRequiresIdempotencyKey()
    {
        await using var harness = await AuthorityHarness.CreateAsync();

        using var response = await harness.Client.PostAsJsonAsync(
            harness.ReclaimUri,
            harness.ReclaimRequest);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("IdempotencyKeyRequired", body.RootElement.GetProperty("code").GetString());
        Assert.Equal(0, harness.Authority.ReclaimExecutionCount);
    }

    [Fact]
    public async Task ReclaimRetryWithSameKeyReturnsOriginalResultWithoutReexecution()
    {
        await using var harness = await AuthorityHarness.CreateAsync();
        const string key = "reclaim-api-retry-1";

        using var firstRequest = CreateJsonRequest(
            harness.ReclaimUri,
            key,
            harness.ReclaimRequest);
        using var first = await harness.Client.SendAsync(firstRequest);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var firstText = await first.Content.ReadAsStringAsync();

        using var retryRequest = CreateJsonRequest(
            harness.ReclaimUri,
            key,
            harness.ReclaimRequest);
        using var retry = await harness.Client.SendAsync(retryRequest);
        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
        var retryText = await retry.Content.ReadAsStringAsync();

        Assert.Equal(firstText, retryText);
        using var body = JsonDocument.Parse(retryText);
        Assert.Equal("ReservationReclaimed", body.RootElement.GetProperty("code").GetString());
        Assert.Equal(1, harness.Authority.ReclaimExecutionCount);
    }

    [Fact]
    public async Task ReclaimKeyReuseWithDifferentRequestReturnsStableConflict()
    {
        await using var harness = await AuthorityHarness.CreateAsync();
        const string key = "reclaim-api-conflict-1";

        using var firstRequest = CreateJsonRequest(
            harness.ReclaimUri,
            key,
            harness.ReclaimRequest);
        using var first = await harness.Client.SendAsync(firstRequest);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        var changed = harness.ReclaimRequest with
        {
            ExpectedGeneration = harness.ReclaimRequest.ExpectedGeneration + 1
        };
        using var conflictingRequest = CreateJsonRequest(harness.ReclaimUri, key, changed);
        using var conflict = await harness.Client.SendAsync(conflictingRequest);

        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        using var body = JsonDocument.Parse(await conflict.Content.ReadAsStringAsync());
        Assert.Equal("IdempotencyKeyConflict", body.RootElement.GetProperty("code").GetString());
        Assert.Equal(1, harness.Authority.ReclaimExecutionCount);
    }

    private static HttpRequestMessage CreateJsonRequest<T>(
        string uri,
        string idempotencyKey,
        T request)
    {
        var message = new HttpRequestMessage(HttpMethod.Post, uri)
        {
            Content = JsonContent.Create(request)
        };
        message.Headers.TryAddWithoutValidation("Idempotency-Key", idempotencyKey);
        return message;
    }

    private sealed class AuthorityHarness : IAsyncDisposable
    {
        private readonly WebApplication _app;

        private AuthorityHarness(
            WebApplication app,
            HttpClient client,
            FakeAuthorityStore authority,
            WorldId worldId,
            AcquireWorldReservationRequest acquireRequest,
            ReclaimWorldReservationRequest reclaimRequest)
        {
            _app = app;
            Client = client;
            Authority = authority;
            WorldId = worldId;
            AcquireRequest = acquireRequest;
            ReclaimRequest = reclaimRequest;
        }

        public HttpClient Client { get; }
        public FakeAuthorityStore Authority { get; }
        public WorldId WorldId { get; }
        public AcquireWorldReservationRequest AcquireRequest { get; }
        public ReclaimWorldReservationRequest ReclaimRequest { get; }
        public string AcquireUri => $"/api/v1/worlds/{WorldId.Value:D}/reservation/acquire";
        public string ReclaimUri => $"/api/v1/worlds/{WorldId.Value:D}/reservation/reclaim";

        public static async Task<AuthorityHarness> CreateAsync()
        {
            var now = new DateTimeOffset(2026, 7, 22, 12, 0, 0, TimeSpan.Zero);
            var identity = new VerifiedExternalIdentity(
                new ExternalIdentityRef("steam", "76561198000000001"));
            var sessions = new StewardSessionService(new SessionStore(), () => now);
            var tokens = await sessions.CreateSessionAsync(identity, "device-a");
            var authorityStore = new FakeAuthorityStore();
            var authority = new SharedWorldAuthorityService(authorityStore, () => now);

            var builder = WebApplication.CreateBuilder(new WebApplicationOptions
            {
                EnvironmentName = "Testing"
            });
            builder.WebHost.UseTestServer();
            builder.Services.AddSingleton(sessions);
            builder.Services.AddSingleton(authority);

            var app = builder.Build();
            app.UseStewardApiProblemHandling();
            app.MapStewardAuthorityApiV1();
            await app.StartAsync();

            var client = app.GetTestClient();
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
                "Bearer",
                tokens.AccessToken);

            var worldId = WorldId.New();
            var stateRevisionId = RevisionId.New();
            var acquireRequest = new AcquireWorldReservationRequest(
                "device-a",
                stateRevisionId.Value,
                null);
            var reclaimRequest = new ReclaimWorldReservationRequest(Guid.NewGuid(), 7);

            return new AuthorityHarness(
                app,
                client,
                authorityStore,
                worldId,
                acquireRequest,
                reclaimRequest);
        }

        public ValueTask DisposeAsync()
        {
            Client.Dispose();
            return _app.DisposeAsync();
        }
    }

    private sealed class FakeAuthorityStore : ISharedWorldAuthorityStore
    {
        private readonly object _gate = new();
        private readonly Dictionary<string, AcquireEntry> _acquires = new(StringComparer.Ordinal);
        private readonly Dictionary<string, ReclaimEntry> _reclaims = new(StringComparer.Ordinal);

        public int AcquireExecutionCount { get; private set; }
        public int ReclaimExecutionCount { get; private set; }

        public Task<IdempotentMutationResult<AcquireSharedWorldReservationResult>> AcquireIdempotentAsync(
            ExternalIdentityRef caller,
            WorldId worldId,
            string installationId,
            SharedWorldHead expectedHead,
            StewardIdempotencyKey idempotencyKey,
            DateTimeOffset serverNow,
            SharedWorldAuthorityOptions options,
            CancellationToken cancellationToken = default)
        {
            lock (_gate)
            {
                var request = new AcquireRequest(worldId, installationId, expectedHead);
                if (_acquires.TryGetValue(idempotencyKey.Value, out var existing))
                {
                    return Task.FromResult(existing.Request == request
                        ? new IdempotentMutationResult<AcquireSharedWorldReservationResult>(
                            IdempotentMutationStatus.Replayed,
                            existing.Result)
                        : new IdempotentMutationResult<AcquireSharedWorldReservationResult>(
                            IdempotentMutationStatus.KeyConflict,
                            null));
                }

                AcquireExecutionCount++;
                var reservation = new SharedWorldReservation(
                    worldId,
                    Guid.NewGuid(),
                    1,
                    caller,
                    installationId,
                    expectedHead,
                    SharedWorldReservationState.Active,
                    serverNow,
                    serverNow,
                    null);
                var result = new AcquireSharedWorldReservationResult(
                    AcquireSharedWorldReservationStatus.Acquired,
                    reservation,
                    expectedHead);
                _acquires.Add(idempotencyKey.Value, new AcquireEntry(request, result));
                return Task.FromResult(new IdempotentMutationResult<AcquireSharedWorldReservationResult>(
                    IdempotentMutationStatus.Executed,
                    result));
            }
        }

        public Task<IdempotentMutationResult<ReclaimSharedWorldReservationResult>> ReclaimIdempotentAsync(
            ExternalIdentityRef caller,
            WorldId worldId,
            Guid expectedSessionId,
            long expectedGeneration,
            StewardIdempotencyKey idempotencyKey,
            DateTimeOffset serverNow,
            SharedWorldAuthorityOptions options,
            CancellationToken cancellationToken = default)
        {
            lock (_gate)
            {
                var request = new ReclaimRequest(worldId, expectedSessionId, expectedGeneration);
                if (_reclaims.TryGetValue(idempotencyKey.Value, out var existing))
                {
                    return Task.FromResult(existing.Request == request
                        ? new IdempotentMutationResult<ReclaimSharedWorldReservationResult>(
                            IdempotentMutationStatus.Replayed,
                            existing.Result)
                        : new IdempotentMutationResult<ReclaimSharedWorldReservationResult>(
                            IdempotentMutationStatus.KeyConflict,
                            null));
                }

                ReclaimExecutionCount++;
                var result = new ReclaimSharedWorldReservationResult(
                    ReclaimSharedWorldReservationStatus.Reclaimed,
                    expectedGeneration,
                    null);
                _reclaims.Add(idempotencyKey.Value, new ReclaimEntry(request, result));
                return Task.FromResult(new IdempotentMutationResult<ReclaimSharedWorldReservationResult>(
                    IdempotentMutationStatus.Executed,
                    result));
            }
        }

        public Task<AcquireSharedWorldReservationResult> AcquireAsync(
            ExternalIdentityRef caller,
            WorldId worldId,
            string installationId,
            SharedWorldHead expectedHead,
            DateTimeOffset serverNow,
            SharedWorldAuthorityOptions options,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<SharedWorldReservation?> GetReservationAsync(
            ExternalIdentityRef caller,
            WorldId worldId,
            DateTimeOffset serverNow,
            SharedWorldAuthorityOptions options,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<SharedWorldHeartbeatStatus> HeartbeatAsync(
            ExternalIdentityRef caller,
            WorldId worldId,
            string installationId,
            Guid sessionId,
            long generation,
            DateTimeOffset serverNow,
            SharedWorldAuthorityOptions options,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<ReclaimSharedWorldReservationResult> ReclaimAsync(
            ExternalIdentityRef caller,
            WorldId worldId,
            Guid expectedSessionId,
            long expectedGeneration,
            DateTimeOffset serverNow,
            SharedWorldAuthorityOptions options,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<CommitSharedWorldResult> CommitAsync(
            ExternalIdentityRef caller,
            CommitSharedWorldCommand command,
            DateTimeOffset serverNow,
            SharedWorldAuthorityOptions options,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<bool> HasUnresolvedWritableResponsibilityAsync(
            WorldId worldId,
            ExternalIdentityRef identity,
            CancellationToken cancellationToken = default)
            => Task.FromResult(false);

        private sealed record AcquireRequest(
            WorldId WorldId,
            string InstallationId,
            SharedWorldHead ExpectedHead);

        private sealed record AcquireEntry(
            AcquireRequest Request,
            AcquireSharedWorldReservationResult Result);

        private sealed record ReclaimRequest(
            WorldId WorldId,
            Guid ExpectedSessionId,
            long ExpectedGeneration);

        private sealed record ReclaimEntry(
            ReclaimRequest Request,
            ReclaimSharedWorldReservationResult Result);
    }

    private sealed class SessionStore : IStewardSessionStore
    {
        private StewardSessionRecord? _session;
        private StewardAccessCredentialRecord? _access;

        public Task ReplaceInstallationSessionAsync(
            StewardSessionRecord session,
            StewardAccessCredentialRecord accessCredential,
            DateTimeOffset replacedAt,
            CancellationToken cancellationToken = default)
        {
            _session = session;
            _access = accessCredential;
            return Task.CompletedTask;
        }

        public Task<StewardAccessContext?> LoadAccessContextAsync(
            string accessTokenHash,
            CancellationToken cancellationToken = default)
            => Task.FromResult<StewardAccessContext?>(
                _session is not null &&
                _access is not null &&
                string.Equals(_access.AccessTokenHash, accessTokenHash, StringComparison.Ordinal)
                    ? new StewardAccessContext(_session, _access)
                    : null);

        public Task<StewardSessionRecord?> LoadRefreshSessionAsync(
            string refreshTokenHash,
            CancellationToken cancellationToken = default)
            => Task.FromResult<StewardSessionRecord?>(null);

        public Task<StoreRotateRefreshSessionStatus> TryRotateRefreshSessionAsync(
            StewardSessionId sessionId,
            string expectedRefreshTokenHash,
            string installationId,
            string newRefreshTokenHash,
            DateTimeOffset newRefreshExpiresAt,
            StewardAccessCredentialRecord newAccessCredential,
            DateTimeOffset rotatedAt,
            CancellationToken cancellationToken = default)
            => Task.FromResult(StoreRotateRefreshSessionStatus.InvalidCredential);

        public Task<bool> TryRevokeRefreshSessionAsync(
            string refreshTokenHash,
            string installationId,
            DateTimeOffset revokedAt,
            CancellationToken cancellationToken = default)
            => Task.FromResult(false);
    }
}
