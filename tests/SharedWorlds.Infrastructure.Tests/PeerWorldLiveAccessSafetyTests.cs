using SharedWorlds.Core.Domain;
using SharedWorlds.Infrastructure.Sessions;

namespace SharedWorlds.Infrastructure.Tests;

public sealed class PeerWorldLiveAccessSafetyTests
{
    [Fact]
    public void CancellationCallbackFailureCannotSuppressTransportRevocationNotification()
    {
        using var registry = new PeerWorldLiveMemberRevocationRegistry();
        var worldId = WorldId.New();
        var member = new UserIdentity("steam", "7002", "Member");
        var token = registry.GetCancellationToken(worldId, 12, member);
        using var registration = token.Register(
            static () => throw new InvalidOperationException("test callback failure"));
        var observerRan = false;
        registry.Revoked += _ => observerRan = true;

        var revoked = registry.Revoke(worldId, 12, member);

        Assert.True(revoked);
        Assert.True(token.IsCancellationRequested);
        Assert.True(observerRan);
        Assert.True(registry.IsRevoked(worldId, 12, member));
    }

    [Fact]
    public void OneRevocationObserverCannotSuppressTheNextObserver()
    {
        using var registry = new PeerWorldLiveMemberRevocationRegistry();
        var worldId = WorldId.New();
        var member = new UserIdentity("steam", "7002", "Member");
        var secondObserverRan = false;
        registry.Revoked += _ => throw new InvalidOperationException("test observer failure");
        registry.Revoked += _ => secondObserverRan = true;

        registry.Revoke(worldId, 12, member);

        Assert.True(secondObserverRan);
    }

    [Fact]
    public async Task AuthorityMutationGateSerializesLiveMutations()
    {
        using var gate = new PeerWorldLiveAuthorityMutationGate();
        using var first = await gate.EnterAsync();
        var secondTask = gate.EnterAsync().AsTask();

        await Task.Delay(20);
        Assert.False(secondTask.IsCompleted);

        first.Dispose();
        using var second = await secondTask.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task AuthorityMutationGateCancelsBlockedEntrantsOnDispose()
    {
        var gate = new PeerWorldLiveAuthorityMutationGate();
        using var first = await gate.EnterAsync();
        var blocked = gate.EnterAsync().AsTask();
        await Task.Delay(20);

        gate.Dispose();

        await Assert.ThrowsAsync<ObjectDisposedException>(async () =>
            await blocked.WaitAsync(TimeSpan.FromSeconds(2)));
        first.Dispose();
    }
}
