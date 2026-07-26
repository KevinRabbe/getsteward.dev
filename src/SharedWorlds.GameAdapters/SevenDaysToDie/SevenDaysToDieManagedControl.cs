namespace SharedWorlds.GameAdapters.SevenDaysToDie;

internal sealed record SevenDaysToDieManagedControl
{
    public SevenDaysToDieManagedControl(int port)
    {
        if (port is < 1 or > 65535)
        {
            throw new ArgumentOutOfRangeException(
                nameof(port),
                port,
                "7 Days to Die managed Telnet port must be between 1 and 65535.");
        }

        Port = port;
    }

    public int Port { get; }

    public override string ToString()
        => $"7DTD managed loopback control port={Port}";
}
