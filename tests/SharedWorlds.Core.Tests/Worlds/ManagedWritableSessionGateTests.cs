using SharedWorlds.Core.Domain;
using SharedWorlds.Core.Worlds;
using Xunit;

namespace SharedWorlds.Core.Tests.Worlds;

public sealed class ManagedWritableSessionGateTests
{
    [Fact]
    public void SecondWorldIsRejectedWhileLeaseIsActive()
    {
        var gate = new ManagedWritableSessionGate();
        var firstWorld = WorldId.New();
        var secondWorld = WorldId.New();
        using var first = gate.Acquire(firstWorld);

        var exception = Assert.Throws<InvalidOperationException>(() => gate.Acquire(secondWorld));

        Assert.Equal(firstWorld, gate.ActiveWorldId);
        Assert.Contains(firstWorld.ToString(), exception.Message);
    }

    [Fact]
    public void ReleasingLeaseAllowsAnotherWorld()
    {
        var gate = new ManagedWritableSessionGate();
        var firstWorld = WorldId.New();
        var secondWorld = WorldId.New();

        using (gate.Acquire(firstWorld))
        {
            Assert.Equal(firstWorld, gate.ActiveWorldId);
        }

        using var second = gate.Acquire(secondWorld);
        Assert.Equal(secondWorld, gate.ActiveWorldId);
    }

    [Fact]
    public void DisposingLeaseTwiceDoesNotReleaseLaterLease()
    {
        var gate = new ManagedWritableSessionGate();
        var firstWorld = WorldId.New();
        var secondWorld = WorldId.New();
        var first = gate.Acquire(firstWorld);
        first.Dispose();

        using var second = gate.Acquire(secondWorld);
        first.Dispose();

        Assert.Equal(secondWorld, gate.ActiveWorldId);
    }
}
