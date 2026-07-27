using SharedWorlds.Backend.Identity;
using SharedWorlds.Backend.Worlds;
using SharedWorlds.Core.Domain;
using Xunit;

namespace SharedWorlds.Backend.Api.Tests;

public sealed class SharedWorldPlayerPresenceServiceTests
{
    [Fact]
    public async Task PublishListAndExpiryStayPresentationOnlyAndBounded()
    {
        var harness = await Harness.CreateAsync();

        var published = await harness.Service.PublishAsync(
            harness.Manager,
            "device-a",
            harness.WorldId);
        var visible = await harness.Service.ListVisibleAsync(
            harness.Manager,
            harness.WorldId);

        Assert.Equal(PublishSharedWorldPlayerPresenceStatus.Published, published);
        var presence = Assert.Single(visible!);
        Assert.Equal(harness.Manager.Subject, presence.Player);
        Assert.Equal("device-a", presence.InstallationId);

        harness.Clock.Now = harness.Clock.Now.AddSeconds(46);
        visible = await harness.Service.ListVisibleAsync(
            harness.Manager,
            harness.WorldId);

        Assert.Empty(visible!);
    }

    [Fact]
    public async Task NonMemberCannotPublishOrReadPresence()
    {
        var harness = await Harness.CreateAsync();
        var outsider = new VerifiedExternalIdentity(
            new ExternalIdentityRef("steam", "76561198000000999"),
            "Outsider");

        var published = await harness.Service.PublishAsync(
            outsider,
            "device-x",
            harness.WorldId);
        var visible = await harness.Service.ListVisibleAsync(
            outsider,
            harness.WorldId);

        Assert.Equal(PublishSharedWorldPlayerPresenceStatus.NotFoundOrUnauthorized, published);
        Assert.Null(visible);
        Assert.Empty(harness.PresenceStore.Rows);
    }

    [Fact]
    public async Task RevocationPendingMemberIsNotVisibleAsPlaying()
    {
        var harness = await Harness.CreateAsync(includePendingMember: true);
        var pending = harness.AccessStore.Members.Single(
            member => member.Status == SharedWorldMemberStatus.RevocationPending);
        await harness.PresenceStore.UpsertAsync(new SharedWorldPlayerPresence(
            harness.WorldId,
            pending.Identity,
            "device-pending",
            harness.Clock.Now));

        var visible = await harness.Service.ListVisibleAsync(
            harness.Manager,
            harness.WorldId);

        Assert.Empty(visible!);
    }

    [Fact]
    public async Task OlderInstallationCannotClearNewerPresenceForSameIdentity()
    {
        var harness = await Harness.CreateAsync();
        await harness.Service.PublishAsync(
            harness.Manager,
            "device-new",
            harness.WorldId);

        var cleared = await harness.Service.ClearAsync(
            harness.Manager,
            "device-old",
            harness.WorldId);

        Assert.False(cleared);
        Assert.Single(harness.PresenceStore.Rows);

        cleared = await harness.Service.ClearAsync(
            harness.Manager,
            "device-new",
            harness.WorldId);

        Assert.True(cleared);
        Assert.Empty(harness.PresenceStore.Rows);
    }

    private sealed class Harness
    {
        private Harness(
            WorldId worldId,
            VerifiedExternalIdentity manager,
            MutableClock clock,
            AccessStore accessStore,
            PresenceStore presenceStore,
            SharedWorldPlayerPresenceService service)
        {
            WorldId = worldId;
            Manager = manager;
            Clock = clock;
            AccessStore = accessStore;
            PresenceStore = presenceStore;
            Service = service;
        }

        public WorldId WorldId { get; }
        public VerifiedExternalIdentity Manager { get; }
        public MutableClock Clock { get; }
        public AccessStore AccessStore { get; }
        public PresenceStore PresenceStore { get; }
        public SharedWorldPlayerPresenceService Service { get; }

        public static async Task<Harness> CreateAsync(bool includePendingMember = false)
        {
            var clock = new MutableClock
            {
                Now = new DateTimeOffset(2026, 7, 27, 3, 0, 0, TimeSpan.Zero)
            };
            var worldId = WorldId.New();
            var managerIdentity = new ExternalIdentityRef("steam", "76561198000000001");
            var manager = new VerifiedExternalIdentity(managerIdentity, "Manager");
            var managerMember = new SharedWorldMember(
                worldId,
                managerIdentity,
                SharedWorldMemberStatus.Active,
                clock.Now);
            var worldStore = new ApiTestHarness.InMemoryWorldStore();
            var created = await worldStore.TryCreateWorldWithManagerAsync(
                new SharedWorldMetadata(
                    worldId,
                    "factorio",
                    "Test World",
                    RevisionId.New(),
                    RevisionId.New(),
                    managerIdentity,
                    clock.Now,
                    clock.Now),
                managerMember);
            Assert.True(created);

            var members = new List<SharedWorldMember> { managerMember };
            if (includePendingMember)
            {
                members.Add(new SharedWorldMember(
                    worldId,
                    new ExternalIdentityRef("steam", "76561198000000002"),
                    SharedWorldMemberStatus.RevocationPending,
                    clock.Now));
            }

            var accessStore = new AccessStore(members);
            var access = new SharedWorldAccessService(
                worldStore,
                accessStore,
                new NoResponsibilityInspector(),
                () => clock.Now);
            var presenceStore = new PresenceStore();
            var service = new SharedWorldPlayerPresenceService(
                access,
                presenceStore,
                () => clock.Now);

            return new Harness(
                worldId,
                manager,
                clock,
                accessStore,
                presenceStore,
                service);
        }
    }

    private sealed class MutableClock
    {
        public DateTimeOffset Now { get; set; }
    }

    private sealed class PresenceStore : ISharedWorldPlayerPresenceStore
    {
        private readonly Dictionary<(WorldId, ExternalIdentityRef), SharedWorldPlayerPresence> _rows = [];

        public IReadOnlyCollection<SharedWorldPlayerPresence> Rows => _rows.Values;

        public Task UpsertAsync(
            SharedWorldPlayerPresence presence,
            CancellationToken cancellationToken = default)
        {
            var key = (presence.WorldId, presence.Player);
            if (!_rows.TryGetValue(key, out var current) || current.UpdatedAt <= presence.UpdatedAt)
            {
                _rows[key] = presence;
            }

            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<SharedWorldPlayerPresence>> ListRecentAsync(
            WorldId worldId,
            DateTimeOffset cutoff,
            CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<SharedWorldPlayerPresence>>(
                _rows.Values
                    .Where(row => row.WorldId == worldId && row.UpdatedAt >= cutoff)
                    .OrderByDescending(row => row.UpdatedAt)
                    .ToArray());

        public Task<bool> DeleteAsync(
            WorldId worldId,
            ExternalIdentityRef player,
            string installationId,
            CancellationToken cancellationToken = default)
        {
            var key = (worldId, player);
            var removed = _rows.TryGetValue(key, out var current) &&
                          string.Equals(current.InstallationId, installationId, StringComparison.Ordinal) &&
                          _rows.Remove(key);
            return Task.FromResult(removed);
        }
    }

    private sealed class AccessStore(IReadOnlyList<SharedWorldMember> members) : ISharedWorldAccessStore
    {
        public IReadOnlyList<SharedWorldMember> Members { get; } = members;

        public Task<IReadOnlyList<SharedWorldMember>> ListMembersAsync(
            WorldId worldId,
            CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<SharedWorldMember>>(
                Members.Where(member => member.WorldId == worldId).ToArray());

        public Task<IReadOnlyList<WorldAccessInvitation>> ListPendingInvitationsForIdentityAsync(
            ExternalIdentityRef identity,
            CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<WorldAccessInvitation>>([]);

        public Task<StoreCreateInvitationStatus> TryCreateInvitationAsync(
            WorldAccessInvitation invitation,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<StoreInvitationResponseStatus> TryAcceptInvitationAsync(
            WorldAccessInvitationId invitationId,
            ExternalIdentityRef invitedIdentity,
            DateTimeOffset respondedAt,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<StoreInvitationResponseStatus> TryDeclineInvitationAsync(
            WorldAccessInvitationId invitationId,
            ExternalIdentityRef invitedIdentity,
            DateTimeOffset respondedAt,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<StoreMemberRevocationStatus> TryRevokeMemberAsync(
            WorldId worldId,
            ExternalIdentityRef expectedAccessManager,
            ExternalIdentityRef targetIdentity,
            bool deferForUnresolvedResponsibility,
            DateTimeOffset changedAt,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<StoreLeaveMemberStatus> TryLeaveWorldAsync(
            WorldId worldId,
            ExternalIdentityRef identity,
            DateTimeOffset changedAt,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<StoreCompletePendingRevocationStatus> TryCompletePendingRevocationAsync(
            WorldId worldId,
            ExternalIdentityRef identity,
            DateTimeOffset changedAt,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<StoreTransferAccessManagerStatus> TryTransferAccessManagerAsync(
            WorldId worldId,
            ExternalIdentityRef expectedAccessManager,
            ExternalIdentityRef targetIdentity,
            DateTimeOffset changedAt,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }

    private sealed class NoResponsibilityInspector : ISharedWorldResponsibilityInspector
    {
        public Task<bool> HasUnresolvedWritableResponsibilityAsync(
            WorldId worldId,
            ExternalIdentityRef identity,
            CancellationToken cancellationToken = default)
            => Task.FromResult(false);
    }
}
