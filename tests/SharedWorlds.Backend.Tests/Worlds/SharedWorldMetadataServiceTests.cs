using SharedWorlds.Backend.Identity;
using SharedWorlds.Backend.Worlds;
using SharedWorlds.Core.Domain;
using Xunit;

namespace SharedWorlds.Backend.Tests.Worlds;

public sealed class SharedWorldMetadataServiceTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 7, 21, 20, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task CreateMakesVerifiedCallerSoleInitialAccessManagerAndActiveMember()
    {
        var store = new InMemoryStore();
        var service = new SharedWorldMetadataService(store, () => Now);
        var caller = Steam("76561198000000001", "Player A");
        var command = CreateCommand();

        var result = await service.CreateSharedWorldAsync(caller, command);
        var world = Assert.IsType<SharedWorldMetadata>(result.World);
        var member = Assert.IsType<SharedWorldMember>(
            await store.LoadMemberAsync(command.WorldId, caller.Subject));

        Assert.Equal(CreateSharedWorldStatus.Created, result.Status);
        Assert.Equal(command.WorldId, world.WorldId);
        Assert.Equal(command.AdapterId, world.AdapterId);
        Assert.Equal(command.DisplayName, world.DisplayName);
        Assert.Equal(command.CurrentStateRevisionId, world.CurrentStateRevisionId);
        Assert.Equal(command.CurrentEnvironmentRevisionId, world.CurrentEnvironmentRevisionId);
        Assert.Equal(caller.Subject, world.AccessManager);
        Assert.Equal(Now, world.CreatedAt);
        Assert.Equal(Now, world.UpdatedAt);
        Assert.Equal(SharedWorldMemberStatus.Active, member.Status);
        Assert.Equal(caller.Subject, member.Identity);
        Assert.Equal(Now, member.AddedAt);
        Assert.Single(store.MembersFor(command.WorldId));
    }

    [Fact]
    public async Task DuplicateWorldCreationCannotReplaceOriginalManagerOrGrantCallerAccess()
    {
        var store = new InMemoryStore();
        var service = new SharedWorldMetadataService(store, () => Now);
        var first = Steam("76561198000000001");
        var second = Steam("76561198000000002");
        var command = CreateCommand();

        var created = await service.CreateSharedWorldAsync(first, command);
        var duplicate = await service.CreateSharedWorldAsync(second, command);
        var persisted = Assert.IsType<SharedWorldMetadata>(await store.LoadWorldAsync(command.WorldId));

        Assert.Equal(CreateSharedWorldStatus.Created, created.Status);
        Assert.Equal(CreateSharedWorldStatus.AlreadyExists, duplicate.Status);
        Assert.Null(duplicate.World);
        Assert.Equal(first.Subject, persisted.AccessManager);
        Assert.Null(await service.GetAccessibleWorldAsync(second, command.WorldId));
        Assert.Single(store.MembersFor(command.WorldId));
    }

    [Fact]
    public async Task NonMemberCannotReadOrListExistingWorld()
    {
        var store = new InMemoryStore();
        var service = new SharedWorldMetadataService(store, () => Now);
        var manager = Steam("76561198000000001");
        var outsider = Steam("76561198000000002");
        var command = CreateCommand();
        await service.CreateSharedWorldAsync(manager, command);

        var read = await service.GetAccessibleWorldAsync(outsider, command.WorldId);
        var list = await service.ListAccessibleWorldsAsync(outsider);

        Assert.Null(read);
        Assert.Empty(list);
    }

    [Fact]
    public async Task ActiveMemberCanReadAndListWorldButRevocationPendingMemberCannot()
    {
        var store = new InMemoryStore();
        var service = new SharedWorldMetadataService(store, () => Now);
        var manager = Steam("76561198000000001");
        var active = Steam("76561198000000002");
        var pending = Steam("76561198000000003");
        var command = CreateCommand();
        await service.CreateSharedWorldAsync(manager, command);
        store.AddMember(new SharedWorldMember(
            command.WorldId,
            active.Subject,
            SharedWorldMemberStatus.Active,
            Now));
        store.AddMember(new SharedWorldMember(
            command.WorldId,
            pending.Subject,
            SharedWorldMemberStatus.RevocationPending,
            Now));

        Assert.NotNull(await service.GetAccessibleWorldAsync(active, command.WorldId));
        Assert.Single(await service.ListAccessibleWorldsAsync(active));
        Assert.Null(await service.GetAccessibleWorldAsync(pending, command.WorldId));
        Assert.Empty(await service.ListAccessibleWorldsAsync(pending));
    }

    [Fact]
    public async Task IdentityProviderIsPartOfAuthorizationIdentity()
    {
        var store = new InMemoryStore();
        var service = new SharedWorldMetadataService(store, () => Now);
        var steam = Steam("same-external-id");
        var otherProvider = new VerifiedExternalIdentity(
            new ExternalIdentityRef("other", "same-external-id"));
        var command = CreateCommand();
        await service.CreateSharedWorldAsync(steam, command);

        Assert.NotNull(await service.GetAccessibleWorldAsync(steam, command.WorldId));
        Assert.Null(await service.GetAccessibleWorldAsync(otherProvider, command.WorldId));
    }

    [Fact]
    public async Task InvalidCanonicalIdentifiersAreRejectedBeforePersistence()
    {
        var store = new InMemoryStore();
        var service = new SharedWorldMetadataService(store, () => Now);
        var caller = Steam("76561198000000001");
        var invalid = CreateCommand() with { CurrentStateRevisionId = default };

        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.CreateSharedWorldAsync(caller, invalid));

        Assert.Empty(store.Worlds);
    }

    private static VerifiedExternalIdentity Steam(string id, string? displayName = null)
        => new(new ExternalIdentityRef("steam", id), displayName);

    private static CreateSharedWorldCommand CreateCommand()
        => new(
            WorldId.New(),
            "factorio",
            "Factory World",
            RevisionId.New(),
            RevisionId.New());

    private sealed class InMemoryStore : ISharedWorldMetadataStore
    {
        private readonly object _gate = new();
        private readonly Dictionary<WorldId, SharedWorldMetadata> _worlds = [];
        private readonly Dictionary<(WorldId WorldId, ExternalIdentityRef Identity), SharedWorldMember> _members = [];

        public IReadOnlyDictionary<WorldId, SharedWorldMetadata> Worlds => _worlds;

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

                Assert.Equal(world.WorldId, accessManager.WorldId);
                Assert.Equal(world.AccessManager, accessManager.Identity);
                Assert.Equal(SharedWorldMemberStatus.Active, accessManager.Status);

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
                    .OrderBy(world => world.DisplayName, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(world => world.WorldId.ToString(), StringComparer.Ordinal)
                    .ToArray();
                return Task.FromResult<IReadOnlyList<SharedWorldMetadata>>(worlds);
            }
        }

        public void AddMember(SharedWorldMember member)
        {
            lock (_gate)
            {
                _members[(member.WorldId, member.Identity)] = member;
            }
        }

        public IReadOnlyList<SharedWorldMember> MembersFor(WorldId worldId)
        {
            lock (_gate)
            {
                return _members.Values
                    .Where(member => member.WorldId == worldId)
                    .ToArray();
            }
        }
    }
}
