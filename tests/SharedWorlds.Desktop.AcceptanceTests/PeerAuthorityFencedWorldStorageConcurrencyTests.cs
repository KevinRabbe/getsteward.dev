using SharedWorlds.Core.Domain;
using SharedWorlds.Infrastructure.Sessions;
using SharedWorlds.Infrastructure.Storage;
using Xunit;

namespace SharedWorlds.Desktop.AcceptanceTests;

public sealed class PeerAuthorityFencedWorldStorageConcurrencyTests : IDisposable
{
    private readonly string _rootPath = Path.Combine(
        Path.GetTempPath(),
        "Steward-tests",
        $"peer-world-mutation-{Guid.NewGuid():N}");

    [Fact]
    public async Task StaleHostCommitPreservesMemberAddedWhileSessionWasRunning()
    {
        var host = new UserIdentity("steam", "1001", "Host");
        var friend = new UserIdentity("steam", "2002", "Friend");
        var worldId = WorldId.New();
        var environmentId = RevisionId.New();
        var baseStateId = RevisionId.New();
        var nextStateId = RevisionId.New();
        var inner = new LocalWorldStorage(_rootPath);
        var fence = new InMemoryFenceStore(
            new PeerAuthorityFence(
                worldId,
                host,
                1,
                baseStateId,
                PeerAuthorityFenceState.Active,
                DateTimeOffset.UtcNow));
        var storage = new PeerAuthorityFencedWorldStorage(inner, fence, host);
        var sessionSnapshot = SharedWorld(
            worldId,
            host,
            environmentId,
            baseStateId,
            [host]);

        await inner.SaveWorldAsync(sessionSnapshot);
        await StoreDirectChildAsync(
            inner,
            worldId,
            host,
            environmentId,
            baseStateId,
            nextStateId);

        // Membership changes after the game has retained its start-of-session World snapshot.
        await storage.SaveWorldAsync(sessionSnapshot with
        {
            Members = [host, friend]
        });

        // Reproduce the historical lifecycle bug exactly: the eventual host commit submits the old
        // whole-World snapshot with only its state head changed.
        await storage.SaveWorldAsync(sessionSnapshot with
        {
            CurrentStateRevisionId = nextStateId
        });

        var persisted = await inner.LoadWorldAsync(worldId);
        Assert.NotNull(persisted);
        Assert.Equal(nextStateId, persisted.CurrentStateRevisionId);
        Assert.Contains(persisted.Members, member => SameUser(member, friend));
        Assert.Equal(2, persisted.Members.Count);
        Assert.Equal(nextStateId, (await fence.LoadAsync(worldId))?.StateRevisionId);
    }

    [Fact]
    public async Task StaleHostCommitCannotResurrectRemovedMemberAfterRestart()
    {
        var host = new UserIdentity("steam", "1001", "Host");
        var removed = new UserIdentity("steam", "2002", "Removed friend");
        var worldId = WorldId.New();
        var environmentId = RevisionId.New();
        var baseStateId = RevisionId.New();
        var nextStateId = RevisionId.New();
        var inner = new LocalWorldStorage(_rootPath);
        var fence = new InMemoryFenceStore(
            new PeerAuthorityFence(
                worldId,
                host,
                7,
                baseStateId,
                PeerAuthorityFenceState.Active,
                DateTimeOffset.UtcNow));
        var storage = new PeerAuthorityFencedWorldStorage(inner, fence, host);
        var sessionSnapshot = SharedWorld(
            worldId,
            host,
            environmentId,
            baseStateId,
            [host, removed],
            generation: 7);

        await inner.SaveWorldAsync(sessionSnapshot);
        await StoreDirectChildAsync(
            inner,
            worldId,
            host,
            environmentId,
            baseStateId,
            nextStateId);

        await storage.SaveWorldAsync(sessionSnapshot with
        {
            Members = [host]
        });

        await storage.SaveWorldAsync(sessionSnapshot with
        {
            CurrentStateRevisionId = nextStateId
        });

        // Read through a new storage instance to prove the membership result is actually durable,
        // rather than an object retained by the decorator in memory.
        var afterRestart = new LocalWorldStorage(_rootPath);
        var persisted = await afterRestart.LoadWorldAsync(worldId);
        Assert.NotNull(persisted);
        Assert.Equal(nextStateId, persisted.CurrentStateRevisionId);
        Assert.Single(persisted.Members);
        Assert.True(SameUser(persisted.Members[0], host));
        Assert.DoesNotContain(persisted.Members, member => SameUser(member, removed));
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_rootPath))
            {
                Directory.Delete(_rootPath, recursive: true);
            }
        }
        catch (IOException)
        {
            // Best-effort test cleanup.
        }
        catch (UnauthorizedAccessException)
        {
            // Best-effort test cleanup.
        }
    }

    private static World SharedWorld(
        WorldId worldId,
        UserIdentity host,
        RevisionId environmentId,
        RevisionId stateId,
        IReadOnlyList<UserIdentity> members,
        ulong generation = 1)
        => new(
            Id: worldId,
            Name: "Mutation race test",
            GameAdapterId: "factorio",
            Members: members,
            CurrentEnvironmentRevisionId: environmentId,
            CurrentStateRevisionId: stateId)
        {
            SharingMode = WorldSharingMode.Shared,
            PeerAuthority = new WorldPeerAuthority(host, generation)
        };

    private static async Task StoreDirectChildAsync(
        LocalWorldStorage storage,
        WorldId worldId,
        UserIdentity host,
        RevisionId environmentId,
        RevisionId baseStateId,
        RevisionId nextStateId)
    {
        var revision = new StateRevision(
            Id: nextStateId,
            WorldId: worldId,
            ParentRevisionId: baseStateId,
            CreatedAt: DateTimeOffset.UtcNow,
            CreatedBy: host,
            AdapterId: "factorio",
            StatePackageId: $"package-{nextStateId}",
            EnvironmentRevisionId: environmentId);
        await using var payload = new MemoryStream([1, 2, 3, 4]);
        await storage.StoreRevisionAsync(revision, payload);
    }

    private static bool SameUser(UserIdentity left, UserIdentity right)
        => string.Equals(left.Provider, right.Provider, StringComparison.OrdinalIgnoreCase) &&
           string.Equals(left.ExternalId, right.ExternalId, StringComparison.Ordinal);

    private sealed class InMemoryFenceStore : IPeerAuthorityActiveRevisionFenceStore
    {
        private readonly object _gate = new();
        private PeerAuthorityFence? _fence;

        public InMemoryFenceStore(PeerAuthorityFence initialFence)
        {
            _fence = initialFence;
        }

        public Task<PeerAuthorityFence?> LoadAsync(
            WorldId worldId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_gate)
            {
                return Task.FromResult(
                    _fence?.WorldId == worldId
                        ? _fence
                        : null);
            }
        }

        public Task SaveAsync(
            PeerAuthorityFence fence,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_gate)
            {
                _fence = fence;
                return Task.CompletedTask;
            }
        }

        public Task<PeerAuthorityFence> AdvanceActiveRevisionAsync(
            WorldId worldId,
            UserIdentity holder,
            ulong generation,
            RevisionId expectedStateRevisionId,
            RevisionId nextStateRevisionId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_gate)
            {
                var current = _fence
                    ?? throw new InvalidDataException("Expected an active peer authority fence.");
                if (current.WorldId != worldId ||
                    current.Generation != generation ||
                    current.State != PeerAuthorityFenceState.Active ||
                    current.StateRevisionId != expectedStateRevisionId ||
                    !SameUser(current.Holder, holder))
                {
                    throw new InvalidDataException("Peer authority fence did not match expected active state.");
                }

                _fence = current with
                {
                    StateRevisionId = nextStateRevisionId,
                    UpdatedAt = DateTimeOffset.UtcNow
                };
                return Task.FromResult(_fence);
            }
        }
    }
}
