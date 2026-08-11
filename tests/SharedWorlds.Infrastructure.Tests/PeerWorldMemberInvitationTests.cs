using SharedWorlds.Core.Abstractions;
using SharedWorlds.Core.Domain;
using SharedWorlds.Core.Sessions;
using SharedWorlds.Infrastructure.Sessions;
using SharedWorlds.Infrastructure.Storage;

namespace SharedWorlds.Infrastructure.Tests;

public sealed class PeerWorldMemberInvitationTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"steward-peer-invites-{Guid.NewGuid():N}");

    [Fact]
    public async Task CanonicalServiceInvitesOnlySupportedPersistedMembersAndContinuesAfterDeliveryFailure()
    {
        Directory.CreateDirectory(_root);
        var storage = new LocalWorldStorage(_root);
        var host = new UserIdentity("steam", "1001", "Host");
        var deliveredMember = new UserIdentity("steam", "1002", "Delivered");
        var failingMember = new UserIdentity("steam", "1003", "Failing");
        var unsupportedMember = new UserIdentity("friends-build", "friend-4", "Other provider");
        var world = new World(
            WorldId.New(),
            "Invite World",
            "fake",
            [host, deliveredMember, failingMember, unsupportedMember],
            RevisionId.New(),
            RevisionId.New())
        {
            SharingMode = WorldSharingMode.Shared,
            PeerAuthority = new WorldPeerAuthority(host, 9)
        };
        await storage.SaveWorldAsync(world);

        var fences = new TestFenceStore
        {
            Record = new PeerAuthorityFence(
                world.Id,
                host,
                9,
                world.CurrentStateRevisionId!.Value,
                PeerAuthorityFenceState.Active,
                DateTimeOffset.UtcNow)
        };
        var transport = new RecordingTransport
        {
            FailingExternalId = failingMember.ExternalId
        };
        var service = new PeerWorldMemberInvitationService(
            storage,
            fences,
            transport);

        var result = await service.InviteCanonicalMembersAsync(world.Id, host);

        Assert.Equal(3, result.CanonicalRemoteMembers);
        Assert.Equal(1, result.Delivered);
        Assert.Equal(1, result.Unsupported);
        Assert.Equal(1, result.Failed);
        Assert.Equal(2, transport.Attempts.Count);
        Assert.All(transport.Attempts, attempt =>
        {
            Assert.Equal(world.Id, attempt.WorldId);
            Assert.Equal(host.ExternalId, attempt.Holder.ExternalId);
            Assert.Equal((ulong)9, attempt.Generation);
            Assert.NotEqual(host.ExternalId, attempt.Member.ExternalId);
        });
        Assert.DoesNotContain(
            transport.Attempts,
            attempt => attempt.Member.ExternalId == unsupportedMember.ExternalId);
    }

    [Fact]
    public async Task CanonicalServiceRejectsStaleFenceBeforeAnyPlatformDelivery()
    {
        Directory.CreateDirectory(_root);
        var storage = new LocalWorldStorage(_root);
        var host = new UserIdentity("steam", "2001", "Host");
        var member = new UserIdentity("steam", "2002", "Member");
        var world = new World(
            WorldId.New(),
            "Stale Invite World",
            "fake",
            [host, member],
            RevisionId.New(),
            RevisionId.New())
        {
            SharingMode = WorldSharingMode.Shared,
            PeerAuthority = new WorldPeerAuthority(host, 4)
        };
        await storage.SaveWorldAsync(world);

        var fences = new TestFenceStore
        {
            Record = new PeerAuthorityFence(
                world.Id,
                host,
                3,
                world.CurrentStateRevisionId!.Value,
                PeerAuthorityFenceState.Active,
                DateTimeOffset.UtcNow)
        };
        var transport = new RecordingTransport();
        var service = new PeerWorldMemberInvitationService(storage, fences, transport);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            service.InviteCanonicalMembersAsync(world.Id, host));

        Assert.Empty(transport.Attempts);
    }

    [Fact]
    public async Task ReadyDecoratorPublishesReadyBeforeInvitationAndNeverRollsReadyBackOnInviteFailure()
    {
        var inner = new RecordingSessionCoordinator();
        var invitation = new ThrowingInvitationService(() => Assert.True(inner.ReadyCalled));
        var localUser = new UserIdentity("steam", "3001", "Host");
        var coordinator = new PeerWorldMemberInvitationSessionCoordinator(
            inner,
            invitation,
            localUser);
        var worldId = WorldId.New();
        var endpoint = new ManagedHostEndpoint(34197, "token");

        await coordinator.MarkHostReadyAsync(worldId, endpoint);

        Assert.True(inner.ReadyCalled);
        Assert.True(invitation.Called);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private sealed class RecordingTransport : IPeerWorldMemberInvitationTransport
    {
        public string? FailingExternalId { get; init; }
        public List<Attempt> Attempts { get; } = [];

        public bool Supports(UserIdentity member)
            => string.Equals(member.Provider, "steam", StringComparison.OrdinalIgnoreCase);

        public Task InviteAsync(
            WorldId worldId,
            UserIdentity expectedHolder,
            ulong authorityGeneration,
            UserIdentity member,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Attempts.Add(new Attempt(worldId, expectedHolder, authorityGeneration, member));
            return string.Equals(member.ExternalId, FailingExternalId, StringComparison.Ordinal)
                ? Task.FromException(new IOException("Synthetic invite delivery failure."))
                : Task.CompletedTask;
        }
    }

    private sealed record Attempt(
        WorldId WorldId,
        UserIdentity Holder,
        ulong Generation,
        UserIdentity Member);

    private sealed class TestFenceStore : IPeerAuthorityFenceStore
    {
        public PeerAuthorityFence? Record { get; init; }

        public Task<PeerAuthorityFence?> LoadAsync(
            WorldId worldId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(
                Record?.WorldId == worldId
                    ? Record
                    : null);
        }

        public Task SaveAsync(
            PeerAuthorityFence fence,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }

    private sealed class ThrowingInvitationService : IPeerWorldMemberInvitationService
    {
        private readonly Action _beforeThrow;

        public ThrowingInvitationService(Action beforeThrow)
            => _beforeThrow = beforeThrow;

        public bool Called { get; private set; }

        public Task<PeerWorldMemberInvitationResult> InviteCanonicalMembersAsync(
            WorldId worldId,
            UserIdentity localHolder,
            CancellationToken cancellationToken = default)
        {
            Called = true;
            _beforeThrow();
            throw new IOException("Synthetic provider outage after Host Ready.");
        }
    }

    private sealed class RecordingSessionCoordinator : IWorldSessionCoordinator
    {
        public bool ReadyCalled { get; private set; }

        public Task<WorldSession> GetSessionAsync(
            WorldId worldId,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<WorldSession> AcquireHostAsync(
            WorldId worldId,
            UserIdentity user,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task MarkHostStartingAsync(
            WorldId worldId,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task MarkHostReadyAsync(
            WorldId worldId,
            ManagedHostEndpoint endpoint,
            CancellationToken cancellationToken = default)
        {
            ReadyCalled = true;
            return Task.CompletedTask;
        }

        public Task EndHostPresenceAsync(
            WorldId worldId,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task RequestHandoffAsync(
            WorldId worldId,
            UserIdentity requestedHost,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task CompleteHandoffAsync(
            WorldId worldId,
            UserIdentity newHost,
            RevisionId committedRevision,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task ReleaseHostAsync(
            WorldId worldId,
            UserIdentity user,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }
}
