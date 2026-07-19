using SharedWorlds.Core.Domain;
using SharedWorlds.Core.Errors;
using SharedWorlds.Core.Sessions;
using SharedWorlds.Infrastructure.Sessions;

namespace SharedWorlds.Infrastructure.Tests;

public sealed class LocalWorldSessionCoordinatorTests
{
    [Fact]
    public async Task AcquireHost_PreventsSecondCanonicalHost()
    {
        var coordinator = new LocalWorldSessionCoordinator();
        var worldId = WorldId.New();
        var first = new UserIdentity("local", "first");
        var second = new UserIdentity("local", "second");

        var session = await coordinator.AcquireHostAsync(worldId, first);

        Assert.Equal(SessionState.Hosting, session.State);
        Assert.Equal(first, session.ActiveHost);

        await Assert.ThrowsAsync<WorldSessionConflictException>(
            () => coordinator.AcquireHostAsync(worldId, second));
    }

    [Fact]
    public async Task ReleaseHost_MakesWorldAvailableAgain()
    {
        var coordinator = new LocalWorldSessionCoordinator();
        var worldId = WorldId.New();
        var first = new UserIdentity("local", "first");
        var second = new UserIdentity("local", "second");

        await coordinator.AcquireHostAsync(worldId, first);
        await coordinator.ReleaseHostAsync(worldId, first);
        var session = await coordinator.AcquireHostAsync(worldId, second);

        Assert.Equal(SessionState.Hosting, session.State);
        Assert.Equal(second, session.ActiveHost);
    }

    [Fact]
    public async Task Handoff_TracksRequestedAndNewHost()
    {
        var coordinator = new LocalWorldSessionCoordinator();
        var worldId = WorldId.New();
        var currentHost = new UserIdentity("local", "current");
        var requestedHost = new UserIdentity("local", "next");

        await coordinator.AcquireHostAsync(worldId, currentHost);
        await coordinator.RequestHandoffAsync(worldId, requestedHost);

        var requested = await coordinator.GetSessionAsync(worldId);
        Assert.Equal(SessionState.HandoffRequested, requested.State);
        Assert.Equal(currentHost, requested.ActiveHost);
        Assert.Equal(requestedHost, requested.RequestedHost);

        await coordinator.CompleteHandoffAsync(
            worldId,
            requestedHost,
            RevisionId.New());

        var completed = await coordinator.GetSessionAsync(worldId);
        Assert.Equal(SessionState.Hosting, completed.State);
        Assert.Equal(requestedHost, completed.ActiveHost);
        Assert.Null(completed.RequestedHost);
    }
}
