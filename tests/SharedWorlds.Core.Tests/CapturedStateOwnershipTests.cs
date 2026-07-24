using SharedWorlds.Core.Abstractions;

namespace SharedWorlds.Core.Tests;

public sealed class CapturedStateOwnershipTests
{
    [Fact]
    public void CapturedPackagesAreDisposableByDefault()
    {
        var captured = new CapturedState(
            new StatePackage("candidate", "candidate.package"),
            DateTimeOffset.UtcNow);

        Assert.True(captured.DeletePackageAfterStore);
    }

    [Fact]
    public void AdapterCanExplicitlyRetainBorrowedPackage()
    {
        var captured = new CapturedState(
            new StatePackage("borrowed", "borrowed.package"),
            DateTimeOffset.UtcNow,
            DeletePackageAfterStore: false);

        Assert.False(captured.DeletePackageAfterStore);
    }
}
