using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using SharedWorlds.Backend.Api;
using SharedWorlds.Backend.Identity;
using SharedWorlds.Backend.PostgreSql;
using SharedWorlds.Backend.Worlds;
using SharedWorlds.Core.Domain;
using SharedWorlds.Infrastructure.Remote;
using Xunit;

namespace SharedWorlds.Backend.PostgreSql.Tests;

public sealed class PostgreSqlTwoDeviceAuthorityApiTests : IAsyncLifetime
{
    private NpgsqlDataSource _dataSource = null!;

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
        await ResetAsync();
    }

    public async Task DisposeAsync() => await _dataSource.DisposeAsync();

    [Fact]
    public async Task CompetingWriterUncertaintyReclaimAndLateGenerationRejectionComposeThroughApi()
    {
        var serverNow = new DateTimeOffset(2026, 7, 22, 12, 0, 0, TimeSpan.Zero);
        var utcNow = new Func<DateTimeOffset>(() => serverNow);
        var worldStore = new PostgreSqlSharedWorldStore(_dataSource);
        var worlds = new SharedWorldMetadataService(worldStore, utcNow);
        var authorityBase = new PostgreSqlSharedWorldAuthorityStore(_dataSource);
        var authorityStore = new PostgreSqlIdempotentReservationAuthorityStore(
            _dataSource,
            new PostgreSqlIdempotentSharedWorldAuthorityStore(_dataSource, authorityBase));
        var authority = new SharedWorldAuthorityService(authorityStore, utcNow);
        var abandon = new SharedWorldReservationAbandonService(
            new PostgreSqlSharedWorldReservationAbandonStore(_dataSource));
        var sessions = new StewardSessionService(
            new PostgreSqlStewardSessionStore(_dataSource),
            utcNow);
        var access = new SharedWorldAccessService(
            worldStore,
            new PostgreSqlSharedWorldAccessStore(_dataSource),
            authorityBase,
            utcNow);

        var identityA = Steam("76561198000000001");
        var identityB = Steam("76561198000000002");
        var worldId = WorldId.New();
        var stateId = RevisionId.New();
        var environmentId = RevisionId.New();
        var head = new StewardRemoteWorldHead(stateId, environmentId);

        Assert.Equal(
            CreateSharedWorldStatus.Created,
            (await worlds.CreateSharedWorldAsync(
                identityA,
                new CreateSharedWorldCommand(
                    worldId,
                    "factorio",
                    "Authority World",
                    stateId,
                    environmentId))).Status);
        var invitation = await access.CreateInvitationAsync(identityA, worldId, identityB.Subject);
        Assert.Equal(CreateWorldAccessInvitationStatus.Created, invitation.Status);
        Assert.NotNull(invitation.Invitation);
        Assert.Equal(
            RespondToWorldAccessInvitationStatus.Accepted,
            await access.AcceptInvitationAsync(identityB, invitation.Invitation.Id));

        var tokensA = await sessions.CreateSessionAsync(identityA, "device-a");
        var tokensB = await sessions.CreateSessionAsync(identityB, "device-b");

        await using var app = await StartAuthorityApiAsync(sessions, authority, abandon);
        using var httpA = app.GetTestClient();
        using var httpB = app.GetTestClient();
        var clientA = new StewardAuthorityClient(httpA);
        var clientB = new StewardAuthorityClient(httpB);
        var abandonB = new StewardReservationAbandonClient(httpB);

        var acquiredA = await clientA.AcquireAsync(
            worldId,
            "device-a",
            head,
            tokensA.AccessToken,
            "a-acquire-1");
        Assert.Equal(RemoteReservationAcquireStatus.Acquired, acquiredA.Status);
        var leaseA = Assert.IsType<StewardRemoteReservation>(acquiredA.Reservation);

        var busyB = await clientB.AcquireAsync(
            worldId,
            "device-b",
            head,
            tokensB.AccessToken,
            "b-acquire-busy-1");
        Assert.Equal(RemoteReservationAcquireStatus.WorldBusy, busyB.Status);

        serverNow = serverNow.AddMinutes(3);
        var uncertainB = await clientB.AcquireAsync(
            worldId,
            "device-b",
            head,
            tokensB.AccessToken,
            "b-acquire-uncertain-1");
        Assert.Equal(RemoteReservationAcquireStatus.WorldUncertain, uncertainB.Status);
        Assert.NotNull(uncertainB.Reservation);
        Assert.Equal(leaseA.SessionId, uncertainB.Reservation.SessionId);
        Assert.Equal(leaseA.Generation, uncertainB.Reservation.Generation);
        Assert.Equal("Uncertain", uncertainB.Reservation.State);

        serverNow = serverNow.AddMinutes(16);
        var reclaimed = await clientB.ReclaimAsync(
            worldId,
            leaseA.SessionId,
            leaseA.Generation,
            tokensB.AccessToken,
            "b-reclaim-1");
        Assert.Equal(RemoteReservationReclaimStatus.Reclaimed, reclaimed.Status);
        Assert.Equal(leaseA.Generation, reclaimed.InvalidatedGeneration);

        var acquiredB = await clientB.AcquireAsync(
            worldId,
            "device-b",
            head,
            tokensB.AccessToken,
            "b-acquire-after-reclaim-1");
        Assert.Equal(RemoteReservationAcquireStatus.Acquired, acquiredB.Status);
        var leaseB = Assert.IsType<StewardRemoteReservation>(acquiredB.Reservation);
        Assert.True(leaseB.Generation > leaseA.Generation);
        Assert.NotEqual(leaseA.SessionId, leaseB.SessionId);

        Assert.Equal(
            RemoteReservationHeartbeatStatus.ReservationMismatch,
            await clientA.HeartbeatAsync(
                worldId,
                "device-a",
                leaseA.SessionId,
                leaseA.Generation,
                tokensA.AccessToken));

        var lateCommit = await clientA.CommitAsync(
            worldId,
            "device-a",
            leaseA.SessionId,
            leaseA.Generation,
            head,
            RevisionId.New(),
            environmentId,
            tokensA.AccessToken,
            "a-late-commit-1");
        Assert.Equal(RemoteWorldCommitStatus.ReservationMismatch, lateCommit.Status);

        Assert.Equal(
            RemoteReservationAbandonStatus.Abandoned,
            await abandonB.AbandonAsync(
                worldId,
                "device-b",
                leaseB.SessionId,
                leaseB.Generation,
                tokensB.AccessToken));
        Assert.Null(await authorityBase.GetReservationAsync(
            identityB.Subject,
            worldId,
            serverNow,
            SharedWorldAuthorityOptions.FirstReleaseDefaults));
    }

    private static async Task<WebApplication> StartAuthorityApiAsync(
        StewardSessionService sessions,
        SharedWorldAuthorityService authority,
        SharedWorldReservationAbandonService abandon)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = "Testing"
        });
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton(sessions);
        builder.Services.AddSingleton(authority);
        builder.Services.AddSingleton(abandon);

        var app = builder.Build();
        app.UseStewardApiProblemHandling();
        app.MapStewardAuthorityApiV1();
        app.MapStewardReservationAbandonApiV1();
        await app.StartAsync();
        return app;
    }

    private async Task ResetAsync()
    {
        await using var command = _dataSource.CreateCommand(
            "TRUNCATE TABLE steward_access_credentials, steward_auth_sessions, " +
            "steward_authority_idempotency, steward_object_cleanup_queue, " +
            "steward_world_reservations, steward_package_transfers, steward_world_invitations, " +
            "steward_state_revisions, steward_environment_revisions, steward_world_members, " +
            "steward_shared_worlds CASCADE;");
        await command.ExecuteNonQueryAsync();
    }

    private static VerifiedExternalIdentity Steam(string id)
        => new(new ExternalIdentityRef("steam", id));
}
