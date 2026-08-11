using SharedWorlds.Core.Abstractions;
using SharedWorlds.Core.Domain;
using SharedWorlds.Infrastructure.Sessions;
using Xunit;

namespace SharedWorlds.Infrastructure.Tests;

public sealed class PeerManagedHostPresenceEndTests
{
    [Fact]
    public async Task EndRemovesPresenceBeforeNotifyingObservers()
    {
        var registry = new PeerManagedHostPresenceRegistry();
        var worldId = new WorldId(Guid.NewGuid());
        var holder = new UserIdentity("steam", "76561198000000001", "Host");
        var starting = await registry.MarkStartingAsync(worldId, holder, 7);

        PeerManagedHostPresence? ended = null;
        PeerManagedHostPresence? visibleDuringNotification = starting;
        registry.Ended += presence =>
        {
            ended = presence;
            visibleDuringNotification = registry.GetAsync(worldId).GetAwaiter().GetResult();
        };

        await registry.EndAsync(worldId, holder, 7);

        Assert.Equal(starting, ended);
        Assert.Null(visibleDuringNotification);
        Assert.Null(await registry.GetAsync(worldId));
    }

    [Fact]
    public async Task MismatchedEndDoesNotNotifyOrRemoveCurrentPresence()
    {
        var registry = new PeerManagedHostPresenceRegistry();
        var worldId = new WorldId(Guid.NewGuid());
        var holder = new UserIdentity("steam", "76561198000000002", "Host");
        await registry.MarkStartingAsync(worldId, holder, 11);
        var notifications = 0;
        registry.Ended += _ => notifications++;

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            registry.EndAsync(worldId, holder, 12));

        Assert.Equal(0, notifications);
        Assert.NotNull(await registry.GetAsync(worldId));
    }

    [Fact]
    public async Task MissingPresenceEndIsIdempotentAndDoesNotNotify()
    {
        var registry = new PeerManagedHostPresenceRegistry();
        var worldId = new WorldId(Guid.NewGuid());
        var holder = new UserIdentity("steam", "76561198000000003", "Host");
        var notifications = 0;
        registry.Ended += _ => notifications++;

        await registry.EndAsync(worldId, holder, 1);

        Assert.Equal(0, notifications);
    }
}
