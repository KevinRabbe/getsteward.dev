using SharedWorlds.Core.Abstractions;

namespace SharedWorlds.GameAdapters.Palworld;

public sealed partial class PalworldAdapter : IManagedHostEndpointProvider, IManualDirectConnectProvider
{
    // Palworld's documented dedicated-server listen port is 8211 unless -port overrides it. Steward's
    // currently qualified managed-host launch does not override the game port, so publish exactly that
    // native endpoint and nothing about REST/admin control.
    internal const int ManagedGamePort = 8211;

    ManagedHostEndpoint? IManagedHostEndpointProvider.GetManagedHostEndpoint(GameSessionHandle session)
    {
        ArgumentNullException.ThrowIfNull(session);
        return _managedHosts.ContainsKey(session.ProcessId)
            ? new ManagedHostEndpoint(Port: ManagedGamePort)
            : null;
    }

    ManualDirectConnectInstruction IManualDirectConnectProvider.GetManualDirectConnectInstruction(
        HostConnection host)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentException.ThrowIfNullOrWhiteSpace(host.Address);
        if (host.Port is not (>= 1 and <= 65535))
        {
            throw new InvalidOperationException(
                "Palworld manual Join requires a ready host address and game port.");
        }

        var endpoint = $"{host.Address}:{host.Port.Value}";
        return new ManualDirectConnectInstruction(
            endpoint,
            $"Open Palworld, choose Join Multiplayer, and enter {endpoint} in the IP:port field.");
    }
}
