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
    public async Task PublishThenGetReturnsReadyPresenceForExactReservation()
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
            Assert.Equal("HostPresencePublished", await ReadCodeAsync(publish));
        }

        using var get = await harness.Client.GetAsync(
            $"/api/v1/worlds/{harness.Reservation.WorldId.Value:D}/host-presence");
        Assert.Equal(HttpStatusCode.OK, get.StatusCode);

        using var body = JsonDocument.Parse(await get.Content.ReadAsStringAsync());
        Assert.Equal("HostPresence", body.RootElement.GetProperty("code").GetString());
        var data = body.RootElement.GetProperty("data");
        Assert.Equal(harness.Reservation.WorldId.Value, data.GetProperty("worldId").GetGuid());
        Assert.Equal(harness.Reservation.SessionId, data.GetProperty("reservationSessionId").GetGuid());
        Assert.Equal(harness.Reservation.Generation, data.GetProperty("reservationGeneration").GetInt64());
        Assert.Equal("device-a", data.GetProperty("hostInstallationId").GetString());
        Assert.Equal("Ready", data.GetProperty("state").GetString());
        Assert.Equal("203.0.113.20", data.GetProperty("address").GetString());
        Assert.Equal(34197, data.GetProperty("port").GetInt32());
        Assert.Equal("join-token", data.GetProperty("joinToken").GetString());
        Assert.Equal(harness.Clock.Now, data.GetProperty("updatedAt").GetDateTimeOffset());
    }

    [Fact]
    public async Task ReadyWithoutAddressFailsClosed()
    {
        await using var harness = await HostPresenceHarness.CreateAsync();

        using var response = await harness.Client.PutAsJsonAsync(
            $"/api/v1/worlds/{harness.Reservation.WorldId.Value:D}/host-presence",
            new
            {
                reservationSessionId = harness.Reservation.SessionId,
                reservationGeneration = harness.Reservation.Generation,
                state = "Ready",
                address = (string?)null,
                port = 34197,
                joinToken = (string?)null
            });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("InvalidHostPresence", await ReadCodeAsync(response));
        Assert.Null(harness.PresenceStore.Presence);
    }

    [Fact]
    public async Task ReservationMismatchReturnsConflictWithoutPublishing()
    {
        await using var harness = await HostPresenceHarness.CreateAsync();

        using var response = await harness.Client.PutAsJsonAsync(
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

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("ReservationMismatch", await ReadCodeAsync(response));
        Assert.Null(harness.PresenceStore.Presence);
    }

    [Fact]
    public async Task ExpiredPresenceIsNotVisibleThroughApi()
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
        Assert.Equal("NotFound", await ReadCodeAsync(get));
    }

    [Fact]
    public async Task ClearRequiresExactCallerReservationIdentity()
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

        using (var wrongGeneration = await harness.Client.DeleteAsync(
                   $"/api/v1/worlds/{harness.Reservation.WorldId.Value:D}/host-presence/{harness.Reservation.SessionId:D}/{harness.Reservation.Generation + 1}"))
        {
            Assert.Equal(HttpStatusCode.NotFound, wrongGeneration.StatusCode);
            Assert.NotNull(harness.PresenceStore.Presence);
        }

        using var clear = await harness.Client.DeleteAsync(
            $"/api/v1/worlds/{harness.Reservation.WorldId.Value:D}/host-presence/{harness.Reservation.SessionId:D}/{harness.Reservation.Generation}");
        Assert.Equal(HttpStatusCode.OK, clear.StatusCode);
        Assert.Equal("HostPresenceCleared", await ReadCodeAsync(clear));
        Assert.Null(harness.PresenceStore.Presence);
    }

    [Fact]
    public async Task MissingBearerCredentialIsUnauthorized()
    {
        await using var harness = await HostPresenceHarness.CreateAsync();
        harness.Client.DefaultRequestHeaders.Authorization = null;

        using var response = await harness.Client.GetAsync(
            $"/api/v1/worlds/{harness.Reservation.WorldId.Value:D}/host-presence");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task EmptyReservationIdentityIsRejectedBeforeServiceMutation()
    {
        await using var harness = await HostPresenceHarness.CreateAsync();

        using var response = await harness.Client.PutAsJsonAsync(
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

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("InvalidReservationIdentity", await ReadCodeAsync(response));
        Assert.Null(harness.PresenceStore.Presence);
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
            MutableClock clock,
            SharedWorldReservation reservation,
            PresenceStore presenceStore)
        {
            _app = app;
            Client = client;
            Clock = clock;
            Reservation = reservation;
            PresenceStore = presenceStore;
        }

        public HttpClient Client { get; }
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
                WorldId.New(),
                Guid.NewGuid(),
                7,
                holder,
                "device-a",
                new SharedWorldHead(RevisionId.New(), RevisionId.New()),
                SharedWorldReservationState.Active,
                clock.Now,
                clock.Now,
                BecameUncertainAt: null);
            var authorityStore = new AuthorityStore { Reservation = reservation };
            var presenceStore = new PresenceStore();
            var sessionStore = new ApiTestHarness.InMemorySessionStore();

            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseTestServer();
            builder.Services.Configure<Microsoft.AspNetCore.Http.Json.JsonOptions>(options =>
                options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
            builder.Services.AddSingleton(new StewardSessionService(
                sessionStore,
                () => clock.Now,
                tokenGenerator: new ApiTestHarness.DeterministicTokenGenerator()));
            builder.Services.AddSingleton(new SharedWorldAuthorityService(
                authorityStore,
                () => clock.Now));
            builder.Services.AddSingleton<ISharedWorldHostPresenceStore>(presenceStore);
            builder.Services.AddSingleton(services => new SharedWorldHostPresenceService(
                services.GetRequiredService<SharedWorldAuthorityService>(),
                services.GetRequiredService<ISharedWorldHostPresenceStore>(),
                () => clock.Now));

            var app = builder.Build();
            app.UseStewardApiProblemHandling();
            app.MapStewardHostPresenceApiV1();
            await app.StartAsync();

            var sessions = app.Services.GetRequiredService<StewardSessionService>();
            var tokens = await sessions.CreateSessionAsync(
                new VerifiedExternalIdentity(holder, "Host"),
                "device-a");
            var client = app.GetTestClient();
            client.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", tokens.AccessToken);

            return new HostPresenceHarness(
                app,
                client,
                clock,
                reservation,
                presenceStore);
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

        public Task UpsertAsync(
            SharedWorldHostPresence presence,
            CancellationToken cancellationToken = default)
        {
            Presence = presence;
            return Task.CompletedTask;
        }

        public Task<SharedWorldHostPresence?> GetAsync(
            WorldId worldId,
            CancellationToken cancellationToken = default)
            => Task.FromResult(Presence?.WorldId == worldId ? Presence : null);

        public Task<bool> DeleteAsync(
            WorldId worldId,
            ExternalIdentityRef holder,
            Guid sessionId,
            long generation,
            CancellationToken cancellationToken = default)
        {
            var matches = Presence is not null &&
                          Presence.WorldId == worldId &&
                          Presence.Holder == holder &&
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
            ExternalIdentityRef caller,
            WorldId worldId,
            DateTimeOffset serverNow,
            SharedWorldAuthorityOptions options,
            CancellationToken cancellationToken = default)
            => Task.FromResult(Reservation?.WorldId == worldId ? Reservation : null);

        public Task<AcquireSharedWorldReservationResult> AcquireAsync(
            ExternalIdentityRef caller,
            WorldId worldId,
            string installationId,
            SharedWorldHead expectedHead,
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
}
