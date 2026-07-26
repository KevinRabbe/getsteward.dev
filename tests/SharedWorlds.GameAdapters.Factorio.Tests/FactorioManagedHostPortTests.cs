using SharedWorlds.Core.Abstractions;

namespace SharedWorlds.GameAdapters.Factorio.Tests;

public sealed class FactorioManagedHostPortTests
{
    [Fact]
    public void ManagedHostUsesFactorioStandardUdpPort()
    {
        Assert.Equal(34197, FactorioAdapter.ManagedGamePort);

        var arguments = FactorioHostingOperations.BuildClientOperationArguments(
            new HostConnection(
                "203.0.113.17",
                FactorioAdapter.ManagedGamePort,
                "session-secret"));

        Assert.Equal(
            ["--mp-connect", "203.0.113.17:34197", "--password", "session-secret"],
            arguments);
    }
}
