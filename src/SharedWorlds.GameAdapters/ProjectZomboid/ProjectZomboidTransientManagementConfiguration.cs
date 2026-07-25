using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace SharedWorlds.GameAdapters.ProjectZomboid;

internal sealed record ProjectZomboidTransientManagementConfiguration(
    int Port,
    byte[] OriginalBytes,
    byte[] RuntimeBytes);

internal static class ProjectZomboidTransientManagementConfigurationBuilder
{
    private const int MaximumConfigurationBytes = 4 * 1024 * 1024;
    private const int TransientPasswordBytes = 32;
    private static readonly byte[] Utf8Bom = [0xEF, 0xBB, 0xBF];
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    internal static string CreateTransientPassword()
        => Convert.ToHexString(RandomNumberGenerator.GetBytes(TransientPasswordBytes));

    internal static ProjectZomboidTransientManagementConfiguration Create(
        byte[] sourceBytes,
        string transientPassword)
    {
        ArgumentNullException.ThrowIfNull(sourceBytes);
        ValidateTransientPassword(transientPassword);
        if (sourceBytes.Length > MaximumConfigurationBytes)
        {
            throw new InvalidDataException(
                $"Project Zomboid server configuration exceeds Steward's {MaximumConfigurationBytes}-byte management safety limit.");
        }

        var originalBytes = sourceBytes.ToArray();
        var hasBom = sourceBytes.AsSpan().StartsWith(Utf8Bom);
        var contentBytes = hasBom
            ? sourceBytes.AsSpan(Utf8Bom.Length)
            : sourceBytes.AsSpan();

        string sourceText;
        try
        {
            sourceText = StrictUtf8.GetString(contentBytes);
        }
        catch (DecoderFallbackException exception)
        {
            throw new InvalidDataException(
                "Project Zomboid server configuration is not valid UTF-8; Steward will not inject transient management credentials into unknown bytes.",
                exception);
        }

        var newline = sourceText.Contains("\r\n", StringComparison.Ordinal)
            ? "\r\n"
            : "\n";
        var hadTrailingNewline = sourceText.EndsWith("\r\n", StringComparison.Ordinal) ||
                                 sourceText.EndsWith("\n", StringComparison.Ordinal);
        var normalized = sourceText.Replace("\r\n", "\n", StringComparison.Ordinal);
        var lines = normalized.Split('\n');
        if (hadTrailingNewline && lines.Length > 0 && lines[^1].Length == 0)
        {
            lines = lines[..^1];
        }

        int? port = null;
        var portEntries = 0;
        var passwordEntries = 0;
        for (var index = 0; index < lines.Length; index++)
        {
            var line = lines[index];
            var equalsIndex = line.IndexOf('=');
            if (equalsIndex < 0)
            {
                continue;
            }

            var key = line[..equalsIndex].Trim();
            if (string.Equals(key, "RCONPort", StringComparison.OrdinalIgnoreCase))
            {
                portEntries++;
                var value = line[(equalsIndex + 1)..].Trim();
                if (!int.TryParse(
                        value,
                        NumberStyles.None,
                        CultureInfo.InvariantCulture,
                        out var parsedPort) ||
                    parsedPort is < 1 or > 65535)
                {
                    throw new InvalidDataException(
                        $"Project Zomboid RCONPort '{value}' is not a valid TCP port.");
                }

                port = parsedPort;
                continue;
            }

            if (!string.Equals(key, "RCONPassword", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            passwordEntries++;
            if (!string.IsNullOrWhiteSpace(line[(equalsIndex + 1)..]))
            {
                throw new InvalidDataException(
                    "Project Zomboid portable server configuration already contains an RCON password. Steward will not overwrite an unknown management credential.");
            }

            lines[index] = line[..(equalsIndex + 1)] + transientPassword;
        }

        if (portEntries != 1 || port is null)
        {
            throw new InvalidDataException(
                $"Project Zomboid server configuration must contain exactly one RCONPort entry; found {portEntries}.");
        }

        if (passwordEntries != 1)
        {
            throw new InvalidDataException(
                $"Project Zomboid server configuration must contain exactly one RCONPassword entry; found {passwordEntries}.");
        }

        var runtimeText = string.Join(newline, lines);
        if (hadTrailingNewline)
        {
            runtimeText += newline;
        }

        var runtimeContent = StrictUtf8.GetBytes(runtimeText);
        var runtimeBytes = hasBom
            ? Combine(Utf8Bom, runtimeContent)
            : runtimeContent;
        return new ProjectZomboidTransientManagementConfiguration(
            port.Value,
            originalBytes,
            runtimeBytes);
    }

    private static void ValidateTransientPassword(string transientPassword)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(transientPassword);
        if (transientPassword.Length != TransientPasswordBytes * 2 ||
            transientPassword.Any(character => !Uri.IsHexDigit(character)))
        {
            throw new ArgumentException(
                "Project Zomboid transient management password must be a 256-bit hexadecimal value.",
                nameof(transientPassword));
        }
    }

    private static byte[] Combine(byte[] prefix, byte[] content)
    {
        var result = new byte[prefix.Length + content.Length];
        prefix.CopyTo(result, 0);
        content.CopyTo(result, prefix.Length);
        return result;
    }
}
