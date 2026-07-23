namespace SharedWorlds.GameAdapters.Palworld;

internal sealed record PalworldRestAcceptanceConfiguration(
    string ConfigPath,
    bool ConfigExists,
    bool RestEnabled,
    int? RestPort,
    bool AdminPasswordConfigured,
    string? SelectedWorldId,
    IReadOnlyList<string> BlockingReasons)
{
    public bool IsUsable =>
        ConfigExists &&
        RestEnabled &&
        RestPort is > 0 and <= 65535 &&
        AdminPasswordConfigured &&
        BlockingReasons.Count == 0;
}

internal static class PalworldRestAcceptanceConfigurationReader
{
    public static PalworldRestAcceptanceConfiguration Read(string dedicatedServerRootPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dedicatedServerRootPath);
        var configPath = Path.Combine(
            dedicatedServerRootPath, "Pal", "Saved", "Config", "WindowsServer", "PalWorldSettings.ini");
        if (!File.Exists(configPath))
        {
            return new(configPath, false, false, null, false, null, ["PalWorldSettings.ini was not found."]);
        }

        string text;
        try
        {
            text = File.ReadAllText(configPath);
        }
        catch (IOException exception)
        {
            return new(configPath, true, false, null, false, null, [$"PalWorldSettings.ini could not be read: {exception.Message}"]);
        }
        catch (UnauthorizedAccessException exception)
        {
            return new(configPath, true, false, null, false, null, [$"PalWorldSettings.ini could not be read: {exception.Message}"]);
        }

        var restEnabledText = ReadValue(text, "RESTAPIEnabled");
        var restPortText = ReadValue(text, "RESTAPIPort");
        var adminPasswordText = ReadValue(text, "AdminPassword");
        var selectedWorldId = ReadSelectedWorldId(dedicatedServerRootPath);
        var blockingReasons = new List<string>();

        var restEnabled = bool.TryParse(restEnabledText, out var parsedRestEnabled) && parsedRestEnabled;
        if (!restEnabled)
        {
            blockingReasons.Add(restEnabledText is null
                ? "RESTAPIEnabled is not configured as true."
                : "RESTAPIEnabled is not true.");
        }

        int? restPort = null;
        if (int.TryParse(restPortText, out var parsedPort) && parsedPort is > 0 and <= 65535)
        {
            restPort = parsedPort;
        }
        else
        {
            blockingReasons.Add("RESTAPIPort is missing or invalid.");
        }

        var adminPasswordConfigured = !string.IsNullOrWhiteSpace(adminPasswordText);
        if (!adminPasswordConfigured)
        {
            blockingReasons.Add("AdminPassword is not configured.");
        }

        return new(configPath, true, restEnabled, restPort, adminPasswordConfigured, selectedWorldId, blockingReasons);
    }

    public static string? ReadAdminPassword(string dedicatedServerRootPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dedicatedServerRootPath);
        var configPath = Path.Combine(
            dedicatedServerRootPath, "Pal", "Saved", "Config", "WindowsServer", "PalWorldSettings.ini");
        if (!File.Exists(configPath))
        {
            return null;
        }

        try
        {
            return ReadValue(File.ReadAllText(configPath), "AdminPassword");
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string? ReadSelectedWorldId(string dedicatedServerRootPath)
    {
        var path = Path.Combine(
            dedicatedServerRootPath,
            "Pal",
            "Saved",
            "Config",
            "WindowsServer",
            "GameUserSettings.ini");
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            foreach (var rawLine in File.ReadLines(path))
            {
                var line = rawLine.Trim();
                const string prefix = "DedicatedServerName=";
                if (!line.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var value = line[prefix.Length..].Trim();
                return string.IsNullOrWhiteSpace(value) ? null : value;
            }
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }

        return null;
    }

    private static string? ReadValue(string text, string key)
    {
        var keyStart = text.IndexOf(key, StringComparison.OrdinalIgnoreCase);
        while (keyStart >= 0)
        {
            var afterKey = keyStart + key.Length;
            var remainder = text[afterKey..].TrimStart();
            if ((keyStart == 0 || !IsKeyCharacter(text[keyStart - 1])) && remainder.StartsWith('='))
            {
                var equalsIndex = text.IndexOf('=', afterKey);
                var valueStart = equalsIndex + 1;
                var valueEnd = valueStart;
                while (valueEnd < text.Length && text[valueEnd] is not (',' or ')' or '\r' or '\n'))
                {
                    valueEnd++;
                }

                var value = text[valueStart..valueEnd].Trim();
                if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
                {
                    value = value[1..^1];
                }

                return value;
            }

            keyStart = text.IndexOf(key, afterKey, StringComparison.OrdinalIgnoreCase);
        }

        return null;
    }

    private static bool IsKeyCharacter(char value) => char.IsLetterOrDigit(value) || value == '_';
}
