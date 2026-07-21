using Npgsql;
using SharedWorlds.Backend.Identity;
using SharedWorlds.Backend.PostgreSql;
using SharedWorlds.Backend.Worlds;
using SharedWorlds.Core.Domain;
using Xunit;

namespace SharedWorlds.Backend.PostgreSql.Tests;

public sealed class PostgreSqlSharedWorldAccessStoreTests : IAsyncLifetime
{
    private static readonly DateTimeOffset Now =
        new(2026, 7, 22, 0, 0, 0, TimeSpan.Zero);

    private NpgsqlDataSource _dataSource = null!;
    private PostgreSqlSharedWorldStore _worldStore = null!;
    private PostgreSqlSharedWorldAccessStore _accessStore = null!;
    private SharedWorldMetadataService _metadata = null!;
    private SharedWorldAccessService _access = null!;
    private TestResponsibilityInspector _responsibility = null!;

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
        _worldStore = new PostgreSqlSharedWorldStore(_dataSource);
        _accessStore = new PostgreSqlSharedWorldAccessStore(_dataSource);
        _metadata = new SharedWorldMetadataService(_worldStore, () => Now);
        _responsibility = new TestResponsibilityInspector();
        var invitationCounter = 0;
        _access = new SharedWorldAccessService(
            _worldStore,
            _accessStore,
            _responsibility,
            () => Now,
            () => new WorldAccessInvitationId(
                new Guid(++invitationCounter, 0, 0, new byte[8])));
    }

    public async Task DisposeAsync()
    {
        await _dataSource.DisposeAsync();
    }

    [Fact]
    public async Task InvitationAcceptancePersistsMembershipAndRemovesPendingSurface()
    {
        await ResetAsync();
        var (manager, worldId) = await CreateWorldAsync();
        var invited = Steam("76561198000000002");

        var created = await _access.CreateInvitationAsync(manager, worldId, invited.Subject);
        var invitation = Assert.IsType<WorldAccessInvitation>(created.Invitation);

        Assert.Single(await _access.ListPendingInvitationsAsync(invited));
        Assert.Null(await _metadata.GetAccessibleWorldAsync(invited, worldId));

        Assert.Equal(
            RespondToWorldAccessInvitationStatus.Accepted,
            await _access.AcceptInvitationAsync(invited, invitation.Id));

        Assert.Empty(await _access.ListPendingInvitationsAsync(invited));
        Assert.NotNull(await _metadata.GetAccessibleWorldAsync(invited, worldId));
        Assert.Equal(2, (await _access.ListMembersAsync(manager, worldId))?.Count);
    }

    [Fact]
    public async Task DuplicateInvitationAndExistingMembershipRemainDeterministicInDatabase()
    {
        await ResetAsync();
        var (manager, worldId) = await CreateWorldAsync();
        var invited = Steam("76561198000000002");

        var first = await _access.CreateInvitationAsync(manager, worldId, invited.Subject);
        var duplicate = await _access.CreateInvitationAsync(manager, worldId, invited.Subject);
        var invitation = Assert.IsType<WorldAccessInvitation>(first.Invitation);
        await _access.AcceptInvitationAsync(invited, invitation.Id);
        var afterAcceptance = await _access.CreateInvitationAsync(manager, worldId, invited.Subject);

        Assert.Equal(CreateWorldAccessInvitationStatus.Created, first.Status);
        Assert.Equal(CreateWorldAccessInvitationStatus.AlreadyInvited, duplicate.Status);
        Assert.Equal(CreateWorldAccessInvitationStatus.TargetAlreadyMember, afterAcceptance.Status);
    }

    [Fact]
    public async Task AccessManagerTransferPersistsAndOldManagerImmediatelyLosesAdminAuthority()
    {
        await ResetAsync();
        var (manager, worldId) = await CreateWorldAsync();
        var member = await AddMemberAsync(manager, worldId, "76561198000000002");
        var target = Steam("76561198000000003");

        Assert.Equal(
            TransferAccessManagerStatus.Transferred,
            await _access.TransferAccessManagerAsync(manager, worldId, member.Subject));

        var persisted = Assert.IsType<SharedWorldMetadata>(await _worldStore.LoadWorldAsync(worldId));
        Assert.Equal(member.Subject, persisted.AccessManager);
        Assert.Equal(
            CreateWorldAccessInvitationStatus.NotAccessManager,
            (await _access.CreateInvitationAsync(manager, worldId, target.Subject)).Status);
        Assert.Equal(
            CreateWorldAccessInvitationStatus.Created,
            (await _access.CreateInvitationAsync(member, worldId, target.Subject)).Status);
    }

    [Fact]
    public async Task RevocationPendingPersistsUntilResponsibilityActuallyResolves()
    {
        await ResetAsync();
        var (manager, worldId) = await CreateWorldAsync();
        var member = await AddMemberAsync(manager, worldId, "76561198000000002");
        _responsibility.MarkUnresolved(worldId, member.Subject);

        Assert.Equal(
            RemoveWorldMemberStatus.RevocationPending,
            await _access.RemoveMemberAsync(manager, worldId, member.Subject));

        var pending = Assert.IsType<SharedWorldMember>(
            await _worldStore.LoadMemberAsync(worldId, member.Subject));
        Assert.Equal(SharedWorldMemberStatus.RevocationPending, pending.Status);
        Assert.Null(await _metadata.GetAccessibleWorldAsync(member, worldId));
        Assert.Equal(
            CompletePendingRevocationStatus.StillUnresolved,
            await _access.CompletePendingRevocationAsync(worldId, member.Subject));

        _responsibility.Resolve(worldId, member.Subject);
        Assert.Equal(
            CompletePendingRevocationStatus.Completed,
            await _access.CompletePendingRevocationAsync(worldId, member.Subject));
        Assert.Null(await _worldStore.LoadMemberAsync(worldId, member.Subject));
    }

    [Fact]
    public async Task MemberLeavePersistsButManagerCannotLeaveBeforeTransfer()
    {
        await ResetAsync();
        var (manager, worldId) = await CreateWorldAsync();
        var member = await AddMemberAsync(manager, worldId, "76561198000000002");

        Assert.Equal(
            LeaveSharedWorldStatus.MustTransferAccessManager,
            await _access.LeaveWorldAsync(manager, worldId));
        Assert.Equal(
            LeaveSharedWorldStatus.Left,
            await _access.LeaveWorldAsync(member, worldId));

        Assert.NotNull(await _metadata.GetAccessibleWorldAsync(manager, worldId));
        Assert.Null(await _worldStore.LoadMemberAsync(worldId, member.Subject));
    }

    private async Task<(VerifiedExternalIdentity Manager, WorldId WorldId)> CreateWorldAsync()
    {
        var manager = Steam("76561198000000001");
        var worldId = WorldId.New();
        var created = await _metadata.CreateSharedWorldAsync(
            manager,
            new CreateSharedWorldCommand(
                worldId,
                "factorio",
                "Factory World",
                RevisionId.New(),
                RevisionId.New()));
        Assert.Equal(CreateSharedWorldStatus.Created, created.Status);
        return (manager, worldId);
    }

    private async Task<VerifiedExternalIdentity> AddMemberAsync(
        VerifiedExternalIdentity manager,
        WorldId worldId,
        string steamId)
    {
        var identity = Steam(steamId);
        var created = await _access.CreateInvitationAsync(manager, worldId, identity.Subject);
        var invitation = Assert.IsType<WorldAccessInvitation>(created.Invitation);
        Assert.Equal(
            RespondToWorldAccessInvitationStatus.Accepted,
            await _access.AcceptInvitationAsync(identity, invitation.Id));
        return identity;
    }

    private async Task ResetAsync()
    {
        await using var command = _dataSource.CreateCommand(
            "TRUNCATE TABLE steward_world_invitations, steward_state_revisions, " +
            "steward_environment_revisions, steward_world_members, steward_shared_worlds CASCADE;");
        await command.ExecuteNonQueryAsync();
    }

    private static VerifiedExternalIdentity Steam(string id)
        => new(new ExternalIdentityRef("steam", id));

    private sealed class TestResponsibilityInspector : ISharedWorldResponsibilityInspector
    {
        private readonly HashSet<(WorldId, ExternalIdentityRef)> _unresolved = [];

        public void MarkUnresolved(WorldId worldId, ExternalIdentityRef identity)
            => _unresolved.Add((worldId, identity));

        public void Resolve(WorldId worldId, ExternalIdentityRef identity)
            => _unresolved.Remove((worldId, identity));

        public Task<bool> HasUnresolvedWritableResponsibilityAsync(
            WorldId worldId,
            ExternalIdentityRef identity,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(_unresolved.Contains((worldId, identity)));
        }
    }
}
