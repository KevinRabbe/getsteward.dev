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

public sealed class StewardAuthorityCommitIdempotencyApiTests
{
    [Fact]
    public async Task CommitRequiresIdempotencyKey()
    {
        await using var harness = await AuthorityHarness.CreateAsync();

        using var response = await harness.Client.PostAsJsonAsync(
            harness.CommitUri,
            harness.Request);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("IdempotencyKeyRequired", body.RootElement.GetProperty("code").GetString());
        Assert.Equal(0, harness.Authority.ExecutionCount);
    }

    [Fact]
    public async Task CommitRetryWithSameKeyReturnsSameBusinessResponseWithoutReexecution()
    {
        await using var harness = await AuthorityHarness.CreateAsync();
        const string key = "commit-api-retry-1";

        using var firstRequest = CreateCommitRequest(harness, key, harness.Request);
        using var first = await harness.Client.SendAsync(firstRequest);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var firstText = await first.Content.ReadAsStringAsync();

        using var retryRequest = CreateCommitRequest(harness, key, harness.Request);
        using var retry = await harness.Client.SendAsync(retryRequest);
        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
        var retryText = await retry.Content.ReadAsStringAsync();

        Assert.Equal(firstText, retryText);
        using var body = JsonDocument.Parse(retryText);
        Assert.Equal("Committed", body.RootElement.GetProperty("code").GetString());
        Assert.Equal(1, harness.Authority.ExecutionCount);
    }

    [Fact]
    public async Task ReusingKeyForDifferentCommitReturnsStableConflict()
    {
        await using var harness = await AuthorityHarness.CreateAsync();
        const string key = "commit-api-conflict-1";

        using var firstRequest = CreateCommitRequest(harness, key, harness.Request);
        using var first = await harness.Client.SendAsync(firstRequest);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        var changed = harness.Request with { CandidateStateRevisionId = Guid.NewGuid() };
        using var conflictingRequest = CreateCommitRequest(harness, key, changed);
        using var conflict = await harness.Client.SendAsync(conflictingRequest);

        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        using var body = JsonDocument.Parse(await conflict.Content.ReadAsStringAsync());
        Assert.Equal("IdempotencyKeyConflict", body.RootElement.GetProperty("code").GetString());
        Assert.Equal(1, harness.Authority.ExecutionCount);
    }

    [Fact]
    public async Task MalformedIdempotencyKeyIsRejectedBeforeAuthorityExecution()
    {
        await using var harness = await AuthorityHarness.CreateAsync();
        using var request = CreateCommitRequest(harness, "contains space", harness.Request);

        using var response = await harness.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("InvalidIdempotencyKey", body.RootElement.GetProperty("code").GetString());
        Assert.Equal(0, harness.Authority.ExecutionCount);
    }

    private static HttpRequestMessage CreateCommitRequest(
        AuthorityHarness harness,
        string idempotencyKey,
        CommitWorldReservationRequest request)
    {
        var message = new HttpRequestMessage(HttpMethod.Post, harness.CommitUri)
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
            CommitWorldReservationRequest request)
        {
            _app = app;
            Client = client;
            Authority = authority;
            WorldId = worldId;
            Request = request;
        }

        public HttpClient Client { get; }
        public FakeAuthorityStore Authority { get; }
        public WorldId WorldId { get; }
        public CommitWorldReservationRequest Request { get; }
        public string CommitUri => $"/api/v1/worlds/{WorldId.Value:D}/reservation/commit";

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

            var worldId = new WorldId(Guid.NewGuid());
            var expectedState = Guid.NewGuid();
            var request = new CommitWorldReservationRequest(
                "device-a",
                Guid.NewGuid(),
                7,
                expectedState,
                null,
                Guid.NewGuid(),
                null);

            return new AuthorityHarness(app, client, authorityStore, worldId, request);
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
        private readonly Dictionary<string, (CommitSharedWorldCommand Command, CommitSharedWorldResult Result)> _results =
            new(StringComparer.Ordinal);

        public int ExecutionCount { get; private set; }

        public Task<IdempotentMutationResult<CommitSharedWorldResult>> CommitIdempotentAsync(
            ExternalIdentityRef caller,
            CommitSharedWorldCommand command,
            StewardIdempotencyKey idempotencyKey,
            DateTimeOffset serverNow,
            SharedWorldAuthorityOptions options,
            CancellationToken cancellationToken = default)
        {
            lock (_gate)
            {
                if (_results.TryGetValue(idempotencyKey.Value, out var existing))
                {
                    return Task.FromResult(existing.Command == command
                        ? new IdempotentMutationResult<CommitSharedWorldResult>(
                            IdempotentMutationStatus.Replayed,
                            existing.Result)
                        : new IdempotentMutationResult<CommitSharedWorldResult>(
                            IdempotentMutationStatus.KeyConflict,
                            null));
                }

                ExecutionCount++;
                var candidateHead = new SharedWorldHead(
                    command.CandidateStateRevisionId,
                    command.CandidateEnvironmentRevisionId);
                var result = new CommitSharedWorldResult(
                    CommitSharedWorldStatus.Committed,
                    candidateHead,
                    candidateHead);
                _results.Add(idempotencyKey.Value, (command, result));
                return Task.FromResult(new IdempotentMutationResult<CommitSharedWorldResult>(
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
