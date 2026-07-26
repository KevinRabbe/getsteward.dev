namespace SharedWorlds.GameAdapters.SevenDaysToDie;

internal sealed record SevenDaysToDieManagedControl
{
    internal const int SecretHexLength = 64;

    public SevenDaysToDieManagedControl(int port, string password)
    {
        if (port is < 1 or > 65535)
        {
            throw new ArgumentOutOfRangeException(
                nameof(port),
                port,
                "7 Days to Die managed Telnet port must be between 1 and 65535.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(password);
        if (password.Length != SecretHexLength ||
            password.Any(static character => !Uri.IsHexDigit(character)))
        {
            throw new ArgumentException(
                $"7 Days to Die managed Telnet password must be exactly {SecretHexLength} hexadecimal characters.",
                nameof(password));
        }

        Port = port;
        Password = password;
    }

    public int Port { get; }

    public string Password { get; }

    public override string ToString()
        => $"7DTD managed control port={Port} password=<redacted>";
}
