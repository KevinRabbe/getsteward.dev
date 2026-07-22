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

public sealed class StewardReservationIdempotencyApiTests
{
    [Fact]
    public async Task AcquireRequiresIdempotencyKey()
    {
        await using var harness = await ReservationHarness.CreateAsync();

        using var response = await harness.Client.PostAsJsonAsync(
            harness.AcquireUri,
            harness.AcquireRequest);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("IdempotencyKeyRequired", await ReadCodeAsync(response));
        Assert.Equal(0, harness.Authority.AcquireExecutionCount);
    }

    [Fact]
    public async Task AcquireRetryReturnsOriginalAcquiredResponse()
    {
        await using var harness = await ReservationHarness.CreateAsync();
        const string key = "acquire-api-retry-1";

        using var first = await SendAsync(harness.Client, harness.AcquireUri, harness.AcquireRequest, key);
        using var retry = await SendAsync(harness.Client, harness.AcquireUri, harness.AcquireRequest, key);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
        Assert.Equal(await first.Content.ReadAsStringAsync(), await retry.Content.ReadAsStringAsync());
        Assert.Equal("ReservationAcquired", await ReadCodeAsync(retry));
        Assert.Equal(1, harness.Authority.AcquireExecutionCount);
    }

    [Fact]
    public async Task AcquireKeyReuseWithDifferentInputReturnsStableConflict()
    {
        await using var harness = await ReservationHarness.CreateAsync();
        const string key = "acquire-api-conflict-1";
        using var first = await SendAsync(harness.Client, harness.AcquireUri, harness.AcquireRequest, key);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        var changed = harness.AcquireRequest with { InstallationId = "device-b" };
        using var conflict = await SendAsync(harness.Client, harness.AcquireUri, changed, key);

        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        Assert.Equal("IdempotencyKeyConflict", await ReadCodeAsync(conflict));
        Assert.Equal(1, harness.Authority.AcquireExecutionCount);
    }

    [Fact]
    public async Task ReclaimRetryReturnsOriginalReclaimedResponse()
    {
        await using var harness = await ReservationHarness.CreateAsync();
        const string key = "reclaim-api-retry-1";

        using var first = await SendAsync(harness.Client, harness.ReclaimUri, harness.ReclaimRequest, key);
        using var retry = await SendAsync(harness.Client, harness.ReclaimUri, harness.ReclaimRequest, key);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
        Assert.Equal(await first.Content.ReadAsStringAsync(), await retry.Content.ReadAsStringAsync());
        Assert.Equal("ReservationReclaimed", await ReadCodeAsync(retry));
        Assert.Equal(1, harness.Authority.ReclaimExecutionCount);
    }

    [Fact]
    public async Task ReclaimKeyReuseWithDifferentInputReturnsStableConflict()
    {
        await using var harness = await ReservationHarness.CreateAsync();
        const string key = "reclaim-api-conflict-1";
        using var first = await SendAsync(harness.Client, harness.ReclaimUri, harness.ReclaimRequest, key);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        var changed = harness.ReclaimRequest with
        {
            ExpectedGeneration = harness.ReclaimRequest.ExpectedGeneration + 1
        };
        using var conflict = await SendAsync(harness.Client, harness.ReclaimUri, changed, key);

        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        Assert.Equal("IdempotencyKeyConflict", await ReadCodeAsync(conflict));
        Assert.Equal(1, harness.Authority.ReclaimExecutionCount);
    }

    private static async Task<HttpResponseMessage> SendAsync<T>(
        HttpClient client,
        string uri,
        T body,
        string idempotencyKey)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, uri)
        {
            Content = JsonContent.Create(body)
        };
        request.Headers.TryAddWithoutValidation("Idempotency-Key", idempotencyKey);
        return await client.SendAsync(request);
    }

    private static async Task<string?> ReadCodeAsync(HttpResponseMessage response)
    {
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return body.RootElement.GetProperty("code").GetString();
    }

    private sealed class ReservationHarness : IAsyncDisposable
    {
        private readonly WebApplication _app;

        private ReservationHarness(
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

        public static async Task<ReservationHarness> CreateAsync()
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

            var worldId = new WorldId(Guid.NewGuid());
            return new ReservationHarness(
                app,
                client,
                authorityStore,
                worldId,
                new AcquireWorldReservationRequest("device-a", Guid.NewGuid(), null),
                new ReclaimWorldReservationRequest(Guid.NewGuid(), 11));
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
        private readonly Dictionary<string, (AcquireInput Input, AcquireSharedWorldReservationResult Result)> _acquires =
            new(StringComparer.Ordinal);
        private readonly Dictionary<string, (ReclaimInput Input, ReclaimSharedWorldReservationResult Result)> _reclaims =
            new(StringComparer.Ordinal);

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
                var input = new AcquireInput(worldId, installationId, expectedHead);
                if (_acquires.TryGetValue(idempotencyKey.Value, out var existing))
                {
                    return Task.FromResult(existing.Input == input
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
                _acquires.Add(idempotencyKey.Value, (input, result));
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
                var input = new ReclaimInput(worldId, expectedSessionId, expectedGeneration);
                if (_reclaims.TryGetValue(idempotencyKey.Value, out var existing))
                {
                    return Task.FromResult(existing.Input == input
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
                    new SharedWorldHead(new RevisionId(Guid.NewGuid()), null));
                _reclaims.Add(idempotencyKey.Value, (input, result));
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

        private sealed record AcquireInput(
            WorldId WorldId,
            string InstallationId,
            SharedWorldHead ExpectedHead);

        private sealed record ReclaimInput(
            WorldId WorldId,
            Guid SessionId,
            long Generation);
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
