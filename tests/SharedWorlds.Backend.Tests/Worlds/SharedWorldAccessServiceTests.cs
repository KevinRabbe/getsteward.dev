using SharedWorlds.Backend.Identity;
using SharedWorlds.Backend.Worlds;
using SharedWorlds.Core.Domain;
using Xunit;

namespace SharedWorlds.Backend.Tests.Worlds;

public sealed class SharedWorldAccessServiceTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 7, 21, 21, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task InvitationGrantsNoAccessUntilInviteeAccepts()
    {
        var fixture = await Fixture.CreateAsync();
        var invited = Steam("76561198000000002");

        var created = await fixture.Access.CreateInvitationAsync(
            fixture.Manager,
            fixture.WorldId,
            invited.Subject);
        var invitation = Assert.IsType<WorldAccessInvitation>(created.Invitation);

        Assert.Equal(CreateWorldAccessInvitationStatus.Created, created.Status);
        Assert.Null(await fixture.Metadata.GetAccessibleWorldAsync(invited, fixture.WorldId));
        Assert.Single(await fixture.Access.ListPendingInvitationsAsync(invited));

        var accepted = await fixture.Access.AcceptInvitationAsync(invited, invitation.Id);

        Assert.Equal(RespondToWorldAccessInvitationStatus.Accepted, accepted);
        Assert.NotNull(await fixture.Metadata.GetAccessibleWorldAsync(invited, fixture.WorldId));
        Assert.Empty(await fixture.Access.ListPendingInvitationsAsync(invited));
        Assert.Equal(
            SharedWorldMemberStatus.Active,
            Assert.IsType<SharedWorldMember>(
                await fixture.Store.LoadMemberAsync(fixture.WorldId, invited.Subject)).Status);
    }

    [Fact]
    public async Task WrongIdentityCannotDiscoverOrAcceptSomebodyElsesInvitation()
    {
        var fixture = await Fixture.CreateAsync();
        var invited = Steam("76561198000000002");
        var outsider = Steam("76561198000000003");
        var created = await fixture.Access.CreateInvitationAsync(
            fixture.Manager,
            fixture.WorldId,
            invited.Subject);
        var invitation = Assert.IsType<WorldAccessInvitation>(created.Invitation);

        var result = await fixture.Access.AcceptInvitationAsync(outsider, invitation.Id);

        Assert.Equal(RespondToWorldAccessInvitationStatus.InvitationNotFound, result);
        Assert.Empty(await fixture.Access.ListPendingInvitationsAsync(outsider));
        Assert.Single(await fixture.Access.ListPendingInvitationsAsync(invited));
        Assert.Null(await fixture.Metadata.GetAccessibleWorldAsync(outsider, fixture.WorldId));
    }

    [Fact]
    public async Task DecliningInvitationNeverCreatesMembership()
    {
        var fixture = await Fixture.CreateAsync();
        var invited = Steam("76561198000000002");
        var created = await fixture.Access.CreateInvitationAsync(
            fixture.Manager,
            fixture.WorldId,
            invited.Subject);
        var invitation = Assert.IsType<WorldAccessInvitation>(created.Invitation);

        var declined = await fixture.Access.DeclineInvitationAsync(invited, invitation.Id);
        var replay = await fixture.Access.DeclineInvitationAsync(invited, invitation.Id);

        Assert.Equal(RespondToWorldAccessInvitationStatus.Declined, declined);
        Assert.Equal(RespondToWorldAccessInvitationStatus.AlreadyResolved, replay);
        Assert.Null(await fixture.Store.LoadMemberAsync(fixture.WorldId, invited.Subject));
        Assert.Null(await fixture.Metadata.GetAccessibleWorldAsync(invited, fixture.WorldId));
    }

    [Fact]
    public async Task OnlyAccessManagerCanInviteOrRemoveMembers()
    {
        var fixture = await Fixture.CreateAsync();
        var member = await fixture.AddMemberAsync("76561198000000002");
        var target = Steam("76561198000000003");

        var invite = await fixture.Access.CreateInvitationAsync(member, fixture.WorldId, target.Subject);
        var remove = await fixture.Access.RemoveMemberAsync(member, fixture.WorldId, fixture.Manager.Subject);

        Assert.Equal(CreateWorldAccessInvitationStatus.NotAccessManager, invite.Status);
        Assert.Equal(RemoveWorldMemberStatus.NotAccessManager, remove);
    }

    [Fact]
    public async Task OutsiderDoesNotLearnWhetherWorldExistsThroughAdministrativeCommands()
    {
        var fixture = await Fixture.CreateAsync();
        var outsider = Steam("76561198000000099");
        var target = Steam("76561198000000002");

        var invite = await fixture.Access.CreateInvitationAsync(outsider, fixture.WorldId, target.Subject);
        var transfer = await fixture.Access.TransferAccessManagerAsync(outsider, fixture.WorldId, target.Subject);
        var remove = await fixture.Access.RemoveMemberAsync(outsider, fixture.WorldId, target.Subject);

        Assert.Equal(CreateWorldAccessInvitationStatus.NotFoundOrUnauthorized, invite.Status);
        Assert.Equal(TransferAccessManagerStatus.NotFoundOrUnauthorized, transfer);
        Assert.Equal(RemoveWorldMemberStatus.NotFoundOrUnauthorized, remove);
    }

    [Fact]
    public async Task DuplicateInvitationAndExistingMembershipDoNotCreateExtraAccessRows()
    {
        var fixture = await Fixture.CreateAsync();
        var invited = Steam("76561198000000002");

        var first = await fixture.Access.CreateInvitationAsync(
            fixture.Manager,
            fixture.WorldId,
            invited.Subject);
        var duplicate = await fixture.Access.CreateInvitationAsync(
            fixture.Manager,
            fixture.WorldId,
            invited.Subject);
        var invitation = Assert.IsType<WorldAccessInvitation>(first.Invitation);
        await fixture.Access.AcceptInvitationAsync(invited, invitation.Id);
        var afterAcceptance = await fixture.Access.CreateInvitationAsync(
            fixture.Manager,
            fixture.WorldId,
            invited.Subject);

        Assert.Equal(CreateWorldAccessInvitationStatus.Created, first.Status);
        Assert.Equal(CreateWorldAccessInvitationStatus.AlreadyInvited, duplicate.Status);
        Assert.Equal(CreateWorldAccessInvitationStatus.TargetAlreadyMember, afterAcceptance.Status);
        Assert.Equal(2, (await fixture.Store.ListMembersAsync(fixture.WorldId)).Count);
    }

    [Fact]
    public async Task AccessManagerTransfersAtomicallyOnlyToActiveMember()
    {
        var fixture = await Fixture.CreateAsync();
        var member = await fixture.AddMemberAsync("76561198000000002");
        var outsider = Steam("76561198000000003");

        var rejected = await fixture.Access.TransferAccessManagerAsync(
            fixture.Manager,
            fixture.WorldId,
            outsider.Subject);
        var transferred = await fixture.Access.TransferAccessManagerAsync(
            fixture.Manager,
            fixture.WorldId,
            member.Subject);
        var persisted = Assert.IsType<SharedWorldMetadata>(
            await fixture.Store.LoadWorldAsync(fixture.WorldId));

        Assert.Equal(TransferAccessManagerStatus.TargetNotActiveMember, rejected);
        Assert.Equal(TransferAccessManagerStatus.Transferred, transferred);
        Assert.Equal(member.Subject, persisted.AccessManager);

        var oldManagerAttempt = await fixture.Access.CreateInvitationAsync(
            fixture.Manager,
            fixture.WorldId,
            outsider.Subject);
        var newManagerAttempt = await fixture.Access.CreateInvitationAsync(
            member,
            fixture.WorldId,
            outsider.Subject);

        Assert.Equal(CreateWorldAccessInvitationStatus.NotAccessManager, oldManagerAttempt.Status);
        Assert.Equal(CreateWorldAccessInvitationStatus.Created, newManagerAttempt.Status);
    }

    [Fact]
    public async Task ManagerCannotRemoveSelfWithoutTransferringManagement()
    {
        var fixture = await Fixture.CreateAsync();

        var result = await fixture.Access.RemoveMemberAsync(
            fixture.Manager,
            fixture.WorldId,
            fixture.Manager.Subject);

        Assert.Equal(RemoveWorldMemberStatus.CannotRemoveAccessManager, result);
        Assert.NotNull(await fixture.Metadata.GetAccessibleWorldAsync(fixture.Manager, fixture.WorldId));
    }

    [Fact]
    public async Task MemberWithoutWritableResponsibilityIsRevokedImmediately()
    {
        var fixture = await Fixture.CreateAsync();
        var member = await fixture.AddMemberAsync("76561198000000002");

        var result = await fixture.Access.RemoveMemberAsync(
            fixture.Manager,
            fixture.WorldId,
            member.Subject);

        Assert.Equal(RemoveWorldMemberStatus.Revoked, result);
        Assert.Null(await fixture.Store.LoadMemberAsync(fixture.WorldId, member.Subject));
        Assert.Null(await fixture.Metadata.GetAccessibleWorldAsync(member, fixture.WorldId));
    }

    [Fact]
    public async Task MemberWithUnresolvedWritableResponsibilityBecomesRevocationPending()
    {
        var fixture = await Fixture.CreateAsync();
        var member = await fixture.AddMemberAsync("76561198000000002");
        fixture.Responsibility.MarkUnresolved(fixture.WorldId, member.Subject);

        var result = await fixture.Access.RemoveMemberAsync(
            fixture.Manager,
            fixture.WorldId,
            member.Subject);
        var persisted = Assert.IsType<SharedWorldMember>(
            await fixture.Store.LoadMemberAsync(fixture.WorldId, member.Subject));

        Assert.Equal(RemoveWorldMemberStatus.RevocationPending, result);
        Assert.Equal(SharedWorldMemberStatus.RevocationPending, persisted.Status);
        Assert.Null(await fixture.Metadata.GetAccessibleWorldAsync(member, fixture.WorldId));
        Assert.Contains(
            await fixture.Store.ListMembersAsync(fixture.WorldId),
            candidate => candidate.Identity == member.Subject &&
                         candidate.Status == SharedWorldMemberStatus.RevocationPending);
    }

    [Fact]
    public async Task ActiveMemberCanSeePeopleButRevocationPendingMemberCannotUseNormalAccessSurface()
    {
        var fixture = await Fixture.CreateAsync();
        var first = await fixture.AddMemberAsync("76561198000000002");
        var second = await fixture.AddMemberAsync("76561198000000003");
        fixture.Responsibility.MarkUnresolved(fixture.WorldId, first.Subject);
        await fixture.Access.RemoveMemberAsync(fixture.Manager, fixture.WorldId, first.Subject);

        var visibleToSecond = await fixture.Access.ListMembersAsync(second, fixture.WorldId);
        var hiddenFromPending = await fixture.Access.ListMembersAsync(first, fixture.WorldId);

        Assert.NotNull(visibleToSecond);
        Assert.Equal(3, visibleToSecond.Count);
        Assert.Null(hiddenFromPending);
    }

    private static VerifiedExternalIdentity Steam(string id)
        => new(new ExternalIdentityRef("steam", id));

    private sealed class Fixture
    {
        private Fixture(
            AccessStore store,
            TestResponsibilityInspector responsibility,
            SharedWorldMetadataService metadata,
            SharedWorldAccessService access,
            VerifiedExternalIdentity manager,
            WorldId worldId)
        {
            Store = store;
            Responsibility = responsibility;
            Metadata = metadata;
            Access = access;
            Manager = manager;
            WorldId = worldId;
        }

        public AccessStore Store { get; }
        public TestResponsibilityInspector Responsibility { get; }
        public SharedWorldMetadataService Metadata { get; }
        public SharedWorldAccessService Access { get; }
        public VerifiedExternalIdentity Manager { get; }
        public WorldId WorldId { get; }

        public static async Task<Fixture> CreateAsync()
        {
            var store = new AccessStore();
            var responsibility = new TestResponsibilityInspector();
            var metadata = new SharedWorldMetadataService(store, () => Now);
            var nextInvitation = 0;
            var access = new SharedWorldAccessService(
                store,
                store,
                responsibility,
                () => Now,
                () => new WorldAccessInvitationId(new Guid(
                    ++nextInvitation,
                    0,
                    0,
                    new byte[8])));
            var manager = Steam("76561198000000001");
            var worldId = WorldId.New();
            var created = await metadata.CreateSharedWorldAsync(
                manager,
                new CreateSharedWorldCommand(
                    worldId,
                    "factorio",
                    "Factory World",
                    RevisionId.New(),
                    RevisionId.New()));
            Assert.Equal(CreateSharedWorldStatus.Created, created.Status);
            return new Fixture(store, responsibility, metadata, access, manager, worldId);
        }

        public async Task<VerifiedExternalIdentity> AddMemberAsync(string steamId)
        {
            var identity = Steam(steamId);
            var created = await Access.CreateInvitationAsync(Manager, WorldId, identity.Subject);
            var invitation = Assert.IsType<WorldAccessInvitation>(created.Invitation);
            Assert.Equal(
                RespondToWorldAccessInvitationStatus.Accepted,
                await Access.AcceptInvitationAsync(identity, invitation.Id));
            return identity;
        }
    }

    private sealed class TestResponsibilityInspector : ISharedWorldResponsibilityInspector
    {
        private readonly HashSet<(WorldId WorldId, ExternalIdentityRef Identity)> _unresolved = [];

        public void MarkUnresolved(WorldId worldId, ExternalIdentityRef identity)
            => _unresolved.Add((worldId, identity));

        public Task<bool> HasUnresolvedWritableResponsibilityAsync(
            WorldId worldId,
            ExternalIdentityRef identity,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(_unresolved.Contains((worldId, identity)));
        }
    }

    private sealed class AccessStore : ISharedWorldMetadataStore, ISharedWorldAccessStore
    {
        private readonly object _gate = new();
        private readonly Dictionary<WorldId, SharedWorldMetadata> _worlds = [];
        private readonly Dictionary<(WorldId WorldId, ExternalIdentityRef Identity), SharedWorldMember> _members = [];
        private readonly Dictionary<WorldAccessInvitationId, WorldAccessInvitation> _invitations = [];

        public Task<bool> TryCreateWorldWithManagerAsync(
            SharedWorldMetadata world,
            SharedWorldMember accessManager,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_gate)
            {
                if (_worlds.ContainsKey(world.WorldId))
                {
                    return Task.FromResult(false);
                }

                _worlds.Add(world.WorldId, world);
                _members.Add((world.WorldId, accessManager.Identity), accessManager);
                return Task.FromResult(true);
            }
        }

        public Task<SharedWorldMetadata?> LoadWorldAsync(
            WorldId worldId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_gate)
            {
                _worlds.TryGetValue(worldId, out var world);
                return Task.FromResult(world);
            }
        }

        public Task<SharedWorldMember?> LoadMemberAsync(
            WorldId worldId,
            ExternalIdentityRef identity,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_gate)
            {
                _members.TryGetValue((worldId, identity), out var member);
                return Task.FromResult(member);
            }
        }

        public Task<IReadOnlyList<SharedWorldMetadata>> ListWorldsForActiveMemberAsync(
            ExternalIdentityRef identity,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_gate)
            {
                var worlds = _members.Values
                    .Where(member => member.Identity == identity &&
                                     member.Status == SharedWorldMemberStatus.Active)
                    .Select(member => _worlds[member.WorldId])
                    .ToArray();
                return Task.FromResult<IReadOnlyList<SharedWorldMetadata>>(worlds);
            }
        }

        public Task<IReadOnlyList<SharedWorldMember>> ListMembersAsync(
            WorldId worldId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_gate)
            {
                var members = _members.Values
                    .Where(member => member.WorldId == worldId)
                    .OrderBy(member => member.AddedAt)
                    .ToArray();
                return Task.FromResult<IReadOnlyList<SharedWorldMember>>(members);
            }
        }

        public Task<IReadOnlyList<WorldAccessInvitation>> ListPendingInvitationsForIdentityAsync(
            ExternalIdentityRef identity,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_gate)
            {
                var invitations = _invitations.Values
                    .Where(invitation => invitation.InvitedIdentity == identity &&
                                         invitation.Status == WorldAccessInvitationStatus.Pending)
                    .OrderBy(invitation => invitation.CreatedAt)
                    .ToArray();
                return Task.FromResult<IReadOnlyList<WorldAccessInvitation>>(invitations);
            }
        }

        public Task<StoreCreateInvitationStatus> TryCreateInvitationAsync(
            WorldAccessInvitation invitation,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_gate)
            {
                if (_members.ContainsKey((invitation.WorldId, invitation.InvitedIdentity)))
                {
                    return Task.FromResult(StoreCreateInvitationStatus.TargetAlreadyMember);
                }

                if (_invitations.Values.Any(existing =>
                        existing.WorldId == invitation.WorldId &&
                        existing.InvitedIdentity == invitation.InvitedIdentity &&
                        existing.Status == WorldAccessInvitationStatus.Pending))
                {
                    return Task.FromResult(StoreCreateInvitationStatus.AlreadyInvited);
                }

                _invitations.Add(invitation.Id, invitation);
                return Task.FromResult(StoreCreateInvitationStatus.Created);
            }
        }

        public Task<StoreInvitationResponseStatus> TryAcceptInvitationAsync(
            WorldAccessInvitationId invitationId,
            ExternalIdentityRef invitedIdentity,
            DateTimeOffset respondedAt,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_gate)
            {
                if (!_invitations.TryGetValue(invitationId, out var invitation) ||
                    invitation.InvitedIdentity != invitedIdentity)
                {
                    return Task.FromResult(StoreInvitationResponseStatus.NotFoundOrNotInvited);
                }

                if (invitation.Status != WorldAccessInvitationStatus.Pending)
                {
                    return Task.FromResult(StoreInvitationResponseStatus.AlreadyResolved);
                }

                _members.Add(
                    (invitation.WorldId, invitedIdentity),
                    new SharedWorldMember(
                        invitation.WorldId,
                        invitedIdentity,
                        SharedWorldMemberStatus.Active,
                        respondedAt));
                _invitations[invitationId] = invitation with
                {
                    Status = WorldAccessInvitationStatus.Accepted,
                    RespondedAt = respondedAt
                };
                return Task.FromResult(StoreInvitationResponseStatus.Completed);
            }
        }

        public Task<StoreInvitationResponseStatus> TryDeclineInvitationAsync(
            WorldAccessInvitationId invitationId,
            ExternalIdentityRef invitedIdentity,
            DateTimeOffset respondedAt,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_gate)
            {
                if (!_invitations.TryGetValue(invitationId, out var invitation) ||
                    invitation.InvitedIdentity != invitedIdentity)
                {
                    return Task.FromResult(StoreInvitationResponseStatus.NotFoundOrNotInvited);
                }

                if (invitation.Status != WorldAccessInvitationStatus.Pending)
                {
                    return Task.FromResult(StoreInvitationResponseStatus.AlreadyResolved);
                }

                _invitations[invitationId] = invitation with
                {
                    Status = WorldAccessInvitationStatus.Declined,
                    RespondedAt = respondedAt
                };
                return Task.FromResult(StoreInvitationResponseStatus.Completed);
            }
        }

        public Task<StoreMemberRevocationStatus> TryRevokeMemberAsync(
            WorldId worldId,
            ExternalIdentityRef expectedAccessManager,
            ExternalIdentityRef targetIdentity,
            bool deferForUnresolvedResponsibility,
            DateTimeOffset changedAt,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_gate)
            {
                if (!_worlds.TryGetValue(worldId, out var world) ||
                    world.AccessManager != expectedAccessManager)
                {
                    return Task.FromResult(StoreMemberRevocationStatus.ManagerChanged);
                }

                if (!_members.TryGetValue((worldId, targetIdentity), out var member) ||
                    member.Status != SharedWorldMemberStatus.Active)
                {
                    return Task.FromResult(StoreMemberRevocationStatus.TargetNotActiveMember);
                }

                _worlds[worldId] = world with { UpdatedAt = changedAt };
                if (deferForUnresolvedResponsibility)
                {
                    _members[(worldId, targetIdentity)] = member with
                    {
                        Status = SharedWorldMemberStatus.RevocationPending
                    };
                    return Task.FromResult(StoreMemberRevocationStatus.RevocationPending);
                }

                _members.Remove((worldId, targetIdentity));
                return Task.FromResult(StoreMemberRevocationStatus.Revoked);
            }
        }

        public Task<StoreTransferAccessManagerStatus> TryTransferAccessManagerAsync(
            WorldId worldId,
            ExternalIdentityRef expectedAccessManager,
            ExternalIdentityRef targetIdentity,
            DateTimeOffset changedAt,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_gate)
            {
                if (!_worlds.TryGetValue(worldId, out var world) ||
                    world.AccessManager != expectedAccessManager)
                {
                    return Task.FromResult(StoreTransferAccessManagerStatus.ManagerChanged);
                }

                if (world.AccessManager == targetIdentity)
                {
                    return Task.FromResult(StoreTransferAccessManagerStatus.AlreadyManager);
                }

                if (!_members.TryGetValue((worldId, targetIdentity), out var member) ||
                    member.Status != SharedWorldMemberStatus.Active)
                {
                    return Task.FromResult(StoreTransferAccessManagerStatus.TargetNotActiveMember);
                }

                _worlds[worldId] = world with
                {
                    AccessManager = targetIdentity,
                    UpdatedAt = changedAt
                };
                return Task.FromResult(StoreTransferAccessManagerStatus.Transferred);
            }
        }
    }
}
