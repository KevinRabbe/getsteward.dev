using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.TestHost;
using SharedWorlds.Backend.Api;
using SharedWorlds.Backend.Identity;
using SharedWorlds.Backend.Worlds;
using SharedWorlds.Core.Domain;
using Xunit;

namespace SharedWorlds.Backend.Api.Tests;

public sealed class StewardHostPresenceApiTests
{
    [Fact]
    public async Task PublishThenGetReturnsOnlyJoinEvidence()
    {
        await using var harness = await HostPresenceHarness.CreateAsync();
        using (var publish = await harness.Client.PutAsJsonAsync(
                   $"/api/v1/worlds/{harness.Reservation.WorldId.Value:D}/host-presence",
                   new
                   {
                       reservationSessionId = harness.Reservation.SessionId,
                       reservationGeneration = harness.Reservation.Generation,
                       state = "Ready",
                       address = "203.0.113.20",
                       port = 34197,
                       joinToken = "join-token"
                   }))
        {
            Assert.Equal(HttpStatusCode.OK, publish.StatusCode);
        }

        using var get = await harness.Client.GetAsync(
            $"/api/v1/worlds/{harness.Reservation.WorldId.Value:D}/host-presence");
        Assert.Equal(HttpStatusCode.OK, get.StatusCode);

        using var body = JsonDocument.Parse(await get.Content.ReadAsStringAsync());
        Assert.Equal("HostPresence", body.RootElement.GetProperty("code").GetString());
        var data = body.RootElement.GetProperty("data");
        Assert.Equal("Ready", data.GetProperty("state").GetString());
        Assert.Equal("203.0.113.20", data.GetProperty("address").GetString());
        Assert.Equal(34197, data.GetProperty("port").GetInt32());
        Assert.Equal("join-token", data.GetProperty("joinToken").GetString());
        Assert.Equal(4, data.EnumerateObject().Count());
        Assert.False(data.TryGetProperty("worldId", out _));
        Assert.False(data.TryGetProperty("reservationSessionId", out _));
        Assert.False(data.TryGetProperty("reservationGeneration", out _));
        Assert.False(data.TryGetProperty("hostInstallationId", out _));
        Assert.False(data.TryGetProperty("updatedAt", out _));
    }

    [Fact]
    public async Task ReadyWithoutAddressUsesObservedIpv4Peer()
    {
        await using var harness = await HostPresenceHarness.CreateAsync();
        using var publish = await harness.Client.PutAsJsonAsync(
            $"/api/v1/worlds/{harness.Reservation.WorldId.Value:D}/host-presence",
            new
            {
                reservationSessionId = harness.Reservation.SessionId,
                reservationGeneration = harness.Reservation.Generation,
                state = "Ready",
                address = (string?)null,
                port = 34197,
                joinToken = "join-token"
            });

        Assert.Equal(HttpStatusCode.OK, publish.StatusCode);
        Assert.Equal("198.51.100.24", harness.PresenceStore.Presence?.Address);
    }

    [Fact]
    public async Task InvalidOrMismatchedPublishFailsWithoutMutation()
    {
        await using var harness = await HostPresenceHarness.CreateAsync();

        using (var invalidPort = await harness.Client.PutAsJsonAsync(
                   $"/api/v1/worlds/{harness.Reservation.WorldId.Value:D}/host-presence",
                   new
                   {
                       reservationSessionId = harness.Reservation.SessionId,
                       reservationGeneration = harness.Reservation.Generation,
                       state = "Ready",
                       address = "203.0.113.20",
                       port = 0,
                       joinToken = (string?)null
                   }))
        {
            Assert.Equal(HttpStatusCode.BadRequest, invalidPort.StatusCode);
            Assert.Equal("InvalidHostPresence", await ReadCodeAsync(invalidPort));
        }

        using var wrongGeneration = await harness.Client.PutAsJsonAsync(
            $"/api/v1/worlds/{harness.Reservation.WorldId.Value:D}/host-presence",
            new
            {
                reservationSessionId = harness.Reservation.SessionId,
                reservationGeneration = harness.Reservation.Generation + 1,
                state = "Starting",
                address = (string?)null,
                port = (int?)null,
                joinToken = (string?)null
            });
        Assert.Equal(HttpStatusCode.Conflict, wrongGeneration.StatusCode);
        Assert.Equal("ReservationMismatch", await ReadCodeAsync(wrongGeneration));
        Assert.Null(harness.PresenceStore.Presence);
    }

    [Fact]
    public async Task ExpiredPresenceIsNotVisible()
    {
        await using var harness = await HostPresenceHarness.CreateAsync();
        using (var publish = await harness.Client.PutAsJsonAsync(
                   $"/api/v1/worlds/{harness.Reservation.WorldId.Value:D}/host-presence",
                   new
                   {
                       reservationSessionId = harness.Reservation.SessionId,
                       reservationGeneration = harness.Reservation.Generation,
                       state = "Ready",
                       address = "203.0.113.20",
                       port = 34197,
                       joinToken = (string?)null
                   }))
        {
            Assert.Equal(HttpStatusCode.OK, publish.StatusCode);
        }

        harness.Clock.Now = harness.Clock.Now.AddSeconds(46);
        using var get = await harness.Client.GetAsync(
            $"/api/v1/worlds/{harness.Reservation.WorldId.Value:D}/host-presence");
        Assert.Equal(HttpStatusCode.NotFound, get.StatusCode);
    }

    [Fact]
    public async Task ClearUsesAuthenticatedInstallationIdentity()
    {
        await using var harness = await HostPresenceHarness.CreateAsync();
        using (var publish = await harness.Client.PutAsJsonAsync(
                   $"/api/v1/worlds/{harness.Reservation.WorldId.Value:D}/host-presence",
                   new
                   {
                       reservationSessionId = harness.Reservation.SessionId,
                       reservationGeneration = harness.Reservation.Generation,
                       state = "Starting",
                       address = (string?)null,
                       port = (int?)null,
                       joinToken = (string?)null
                   }))
        {
            Assert.Equal(HttpStatusCode.OK, publish.StatusCode);
        }

        using var clear = await harness.Client.DeleteAsync(
            $"/api/v1/worlds/{harness.Reservation.WorldId.Value:D}/host-presence/{harness.Reservation.SessionId:D}/{harness.Reservation.Generation}");
        Assert.Equal(HttpStatusCode.OK, clear.StatusCode);
        Assert.Equal("HostPresenceCleared", await ReadCodeAsync(clear));
        Assert.Equal("device-a", harness.PresenceStore.LastDeleteInstallationId);
        Assert.Null(harness.PresenceStore.Presence);
    }

    [Fact]
    public async Task MissingBearerOrEmptyReservationIdentityFailsClosed()
    {
        await using var harness = await HostPresenceHarness.CreateAsync();
        harness.Client.DefaultRequestHeaders.Authorization = null;
        using (var unauthorized = await harness.Client.GetAsync(
                   $"/api/v1/worlds/{harness.Reservation.WorldId.Value:D}/host-presence"))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, unauthorized.StatusCode);
        }

        harness.Client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", harness.AccessToken);
        using var invalid = await harness.Client.PutAsJsonAsync(
            $"/api/v1/worlds/{harness.Reservation.WorldId.Value:D}/host-presence",
            new
            {
                reservationSessionId = Guid.Empty,
                reservationGeneration = 0,
                state = "Starting",
                address = (string?)null,
                port = (int?)null,
                joinToken = (string?)null
            });
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.Equal("InvalidReservationIdentity", await ReadCodeAsync(invalid));
    }

    private static async Task<string?> ReadCodeAsync(HttpResponseMessage response)
    {
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return body.RootElement.GetProperty("code").GetString();
    }

    private sealed class HostPresenceHarness : IAsyncDisposable
    {
        private readonly WebApplication _app;

        private HostPresenceHarness(
            WebApplication app,
            HttpClient client,
            string accessToken,
            MutableClock clock,
            SharedWorldReservation reservation,
            PresenceStore presenceStore)
        {
            _app = app;
            Client = client;
            AccessToken = accessToken;
            Clock = clock;
            Reservation = reservation;
            PresenceStore = presenceStore;
        }

        public HttpClient Client { get; }
        public string AccessToken { get; }
        public MutableClock Clock { get; }
        public SharedWorldReservation Reservation { get; }
        public PresenceStore PresenceStore { get; }

        public static async Task<HostPresenceHarness> CreateAsync()
        {
            var clock = new MutableClock
            {
                Now = new DateTimeOffset(2026, 7, 24, 12, 0, 0, TimeSpan.Zero)
            };
            var holder = new ExternalIdentityRef("steam", "76561198000000001");
            var reservation = new SharedWorldReservation(
                WorldId.New(), Guid.NewGuid(), 7, holder, "device-a",
                new SharedWorldHead(RevisionId.New(), RevisionId.New()),
                SharedWorldReservationState.Active, clock.Now, clock.Now, null);
            var authorityStore = new AuthorityStore { Reservation = reservation };
            var presenceStore = new PresenceStore();

            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseTestServer();
            builder.Services.Configure<Microsoft.AspNetCore.Http.Json.JsonOptions>(options =>
                options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
            builder.Services.AddSingleton(new StewardSessionService(
                new ApiTestHarness.InMemorySessionStore(),
                () => clock.Now,
                tokenGenerator: new ApiTestHarness.DeterministicTokenGenerator()));
            builder.Services.AddSingleton(new SharedWorldAuthorityService(authorityStore, () => clock.Now));
            builder.Services.AddSingleton<ISharedWorldHostPresenceStore>(presenceStore);
            builder.Services.AddSingleton(services => new SharedWorldHostPresenceService(
                services.GetRequiredService<SharedWorldAuthorityService>(),
                services.GetRequiredService<ISharedWorldHostPresenceStore>(),
                () => clock.Now));

            var app = builder.Build();
            app.Use(async (context, next) =>
            {
                context.Connection.RemoteIpAddress = IPAddress.Parse("198.51.100.24");
                await next();
            });
            app.UseStewardApiProblemHandling();
            app.MapStewardHostPresenceApiV1();
            await app.StartAsync();

            var sessions = app.Services.GetRequiredService<StewardSessionService>();
            var tokens = await sessions.CreateSessionAsync(
                new VerifiedExternalIdentity(holder, "Host"), "device-a");
            var client = app.GetTestClient();
            client.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", tokens.AccessToken);

            return new HostPresenceHarness(
                app, client, tokens.AccessToken, clock, reservation, presenceStore);
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await _app.DisposeAsync();
        }
    }

    private sealed class MutableClock
    {
        public DateTimeOffset Now { get; set; }
    }

    public sealed class PresenceStore : ISharedWorldHostPresenceStore
    {
        public SharedWorldHostPresence? Presence { get; private set; }
        public string? LastDeleteInstallationId { get; private set; }

        public Task<bool> TryUpsertAsync(
            SharedWorldHostPresence presence,
            CancellationToken cancellationToken = default)
        {
            Presence = presence;
            return Task.FromResult(true);
        }

        public Task<SharedWorldHostPresence?> GetAsync(
            WorldId worldId,
            CancellationToken cancellationToken = default)
            => Task.FromResult(Presence?.WorldId == worldId ? Presence : null);

        public Task<bool> DeleteAsync(
            WorldId worldId,
            ExternalIdentityRef holder,
            string installationId,
            Guid sessionId,
            long generation,
            CancellationToken cancellationToken = default)
        {
            LastDeleteInstallationId = installationId;
            var matches = Presence is not null &&
                          Presence.WorldId == worldId &&
                          Presence.Holder == holder &&
                          Presence.InstallationId == installationId &&
                          Presence.SessionId == sessionId &&
                          Presence.Generation == generation;
            if (matches)
            {
                Presence = null;
            }

            return Task.FromResult(matches);
        }
    }

    private sealed class AuthorityStore : ISharedWorldAuthorityStore
    {
        public SharedWorldReservation? Reservation { get; init; }

        public Task<SharedWorldReservation?> GetReservationAsync(
            ExternalIdentityRef caller, WorldId worldId, DateTimeOffset serverNow,
            SharedWorldAuthorityOptions options, CancellationToken cancellationToken = default)
            => Task.FromResult(Reservation?.WorldId == worldId ? Reservation : null);

        public Task<AcquireSharedWorldReservationResult> AcquireAsync(
            ExternalIdentityRef caller, WorldId worldId, string installationId,
            SharedWorldHead expectedHead, DateTimeOffset serverNow,
            SharedWorldAuthorityOptions options, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<SharedWorldHeartbeatStatus> HeartbeatAsync(
            ExternalIdentityRef caller, WorldId worldId, string installationId, Guid sessionId,
            long generation, DateTimeOffset serverNow, SharedWorldAuthorityOptions options,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<ReclaimSharedWorldReservationResult> ReclaimAsync(
            ExternalIdentityRef caller, WorldId worldId, Guid expectedSessionId,
            long expectedGeneration, DateTimeOffset serverNow, SharedWorldAuthorityOptions options,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<CommitSharedWorldResult> CommitAsync(
            ExternalIdentityRef caller, CommitSharedWorldCommand command, DateTimeOffset serverNow,
            SharedWorldAuthorityOptions options, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<bool> HasUnresolvedWritableResponsibilityAsync(
            WorldId worldId, ExternalIdentityRef identity,
            CancellationToken cancellationToken = default)
            => Task.FromResult(false);
    }
}
