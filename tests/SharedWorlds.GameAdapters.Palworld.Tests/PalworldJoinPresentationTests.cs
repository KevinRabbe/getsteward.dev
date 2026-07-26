using SharedWorlds.Core.Abstractions;

namespace SharedWorlds.GameAdapters.Palworld.Tests;

public sealed class PalworldJoinPresentationTests
{
    [Fact]
    public void AdapterExposesManualDirectConnectWithoutClaimingAutomaticJoin()
    {
        var adapter = new PalworldAdapter();

        Assert.IsAssignableFrom<IManagedHostEndpointProvider>(adapter);
        Assert.IsAssignableFrom<IManualDirectConnectProvider>(adapter);
        Assert.False(adapter.Capabilities.HasFlag(GameAdapterCapabilities.AutomaticClientJoin));
    }

    [Fact]
    public void ManualDirectConnectUsesPublishedIpAndGamePort()
    {
        var adapter = (IManualDirectConnectProvider)new PalworldAdapter();

        var guidance = adapter.GetManualDirectConnectInstruction(
            new HostConnection("203.0.113.17", 8211));

        Assert.Equal("203.0.113.17:8211", guidance.Endpoint);
        Assert.Contains("Join Multiplayer", guidance.Instruction, StringComparison.Ordinal);
        Assert.Contains(guidance.Endpoint, guidance.Instruction, StringComparison.Ordinal);
    }

    [Fact]
    public void ManagedEndpointIsNotPublishedForUnknownSession()
    {
        var adapter = (IManagedHostEndpointProvider)new PalworldAdapter();

        var endpoint = adapter.GetManagedHostEndpoint(
            new GameSessionHandle(12345, DateTimeOffset.UtcNow));

        Assert.Null(endpoint);
    }

    [Fact]
    public void ManualDirectConnectRejectsMissingGamePort()
    {
        var adapter = (IManualDirectConnectProvider)new PalworldAdapter();

        var exception = Assert.Throws<InvalidOperationException>(() =>
            adapter.GetManualDirectConnectInstruction(new HostConnection("203.0.113.17")));

        Assert.Contains("game port", exception.Message, StringComparison.OrdinalIgnoreCase);
    }
}
