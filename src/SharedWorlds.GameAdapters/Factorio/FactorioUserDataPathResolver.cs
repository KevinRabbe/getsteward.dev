namespace SharedWorlds.GameAdapters.Factorio;

internal static class FactorioUserDataPathResolver
{
    private const string ConfigPathFileName = "config-path.cfg";
    private const string ConfigDirectoryName = "config";
    private const string ConfigFileName = "config.ini";
    private const string SavesDirectoryName = "saves";
    private const string ExecutablePathToken = "__PATH__executable__";
    private const string SystemWriteDataPathToken = "__PATH__system-write-data__";

    internal static string Resolve(
        string installationRoot,
        string executablePath,
        string defaultSystemUserDataPath,
        string userProfilePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(installationRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(defaultSystemUserDataPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(userProfilePath);

        var normalizedInstallationRoot = Path.GetFullPath(installationRoot);
        var normalizedExecutablePath = Path.GetFullPath(executablePath);
        var normalizedDefaultUserDataPath = Path.GetFullPath(defaultSystemUserDataPath);
        var normalizedUserProfilePath = Path.GetFullPath(userProfilePath);

        var configuredPath = TryResolveConfiguredWriteDataPath(
            normalizedInstallationRoot,
            normalizedExecutablePath,
            normalizedDefaultUserDataPath,
            normalizedUserProfilePath);
        if (configuredPath is not null)
        {
            return configuredPath;
        }

        // Factorio's portable ZIP distribution keeps user data beside the installation.
        if (Directory.Exists(Path.Combine(normalizedInstallationRoot, SavesDirectoryName)))
        {
            return normalizedInstallationRoot;
        }

        return normalizedDefaultUserDataPath;
    }

    private static string? TryResolveConfiguredWriteDataPath(
        string installationRoot,
        string executablePath,
        string defaultSystemUserDataPath,
        string userProfilePath)
    {
        foreach (var configFile in EnumerateConfigFiles(
                     installationRoot,
                     executablePath,
                     defaultSystemUserDataPath,
                     userProfilePath))
        {
            var configuredValue = TryReadPathSectionValue(configFile, "write-data");
            if (string.IsNullOrWhiteSpace(configuredValue))
            {
                continue;
            }

            var resolved = TryResolvePathValue(
                configuredValue,
                executablePath,
                defaultSystemUserDataPath,
                userProfilePath,
                relativeBasePath: userProfilePath);
            if (resolved is not null)
            {
                return resolved;
            }
        }

        return null;
    }

    private static IEnumerable<string> EnumerateConfigFiles(
        string installationRoot,
        string executablePath,
        string defaultSystemUserDataPath,
        string userProfilePath)
    {
        var comparer = OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;
        var seen = new HashSet<string>(comparer);

        var configPathFile = Path.Combine(installationRoot, ConfigPathFileName);
        var configuredConfigDirectory = TryReadSimpleValue(configPathFile, "config-path");
        if (!string.IsNullOrWhiteSpace(configuredConfigDirectory))
        {
            var resolvedConfigDirectory = TryResolvePathValue(
                configuredConfigDirectory,
                executablePath,
                defaultSystemUserDataPath,
                userProfilePath,
                relativeBasePath: installationRoot);
            if (resolvedConfigDirectory is not null)
            {
                var configuredFile = Path.Combine(resolvedConfigDirectory, ConfigFileName);
                if (seen.Add(configuredFile))
                {
                    yield return configuredFile;
                }
            }
        }

        var installationConfig = Path.Combine(
            installationRoot,
            ConfigDirectoryName,
            ConfigFileName);
        if (seen.Add(installationConfig))
        {
            yield return installationConfig;
        }

        var systemConfig = Path.Combine(
            defaultSystemUserDataPath,
            ConfigDirectoryName,
            ConfigFileName);
        if (seen.Add(systemConfig))
        {
            yield return systemConfig;
        }
    }

    private static string? TryReadPathSectionValue(string path, string key)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var inPathSection = false;
            foreach (var rawLine in File.ReadLines(path))
            {
                var line = rawLine.Trim();
                if (line.Length == 0 || IsComment(line))
                {
                    continue;
                }

                if (line[0] == '[' && line[^1] == ']')
                {
                    inPathSection = string.Equals(
                        line,
                        "[path]",
                        StringComparison.OrdinalIgnoreCase);
                    continue;
                }

                if (!inPathSection)
                {
                    continue;
                }

                var value = TryParseKeyValue(line, key);
                if (value is not null)
                {
                    return value;
                }
            }
        }
        catch (IOException)
        {
            // Discovery is best-effort. Fall back to the conventional location.
        }
        catch (UnauthorizedAccessException)
        {
            // Discovery is best-effort. Fall back to the conventional location.
        }

        return null;
    }

    private static string? TryReadSimpleValue(string path, string key)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            foreach (var rawLine in File.ReadLines(path))
            {
                var line = rawLine.Trim();
                if (line.Length == 0 || IsComment(line))
                {
                    continue;
                }

                var value = TryParseKeyValue(line, key);
                if (value is not null)
                {
                    return value;
                }
            }
        }
        catch (IOException)
        {
            // Discovery is best-effort. Ignore unreadable optional configuration.
        }
        catch (UnauthorizedAccessException)
        {
            // Discovery is best-effort. Ignore unreadable optional configuration.
        }

        return null;
    }

    private static string? TryParseKeyValue(string line, string expectedKey)
    {
        var separatorIndex = line.IndexOf('=');
        if (separatorIndex <= 0)
        {
            return null;
        }

        var key = line[..separatorIndex].Trim();
        if (!string.Equals(key, expectedKey, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var value = line[(separatorIndex + 1)..].Trim();
        return value.Length == 0 ? null : TrimMatchingQuotes(value);
    }

    private static string? TryResolvePathValue(
        string value,
        string executablePath,
        string defaultSystemUserDataPath,
        string userProfilePath,
        string relativeBasePath)
    {
        try
        {
            var executableDirectory = Path.GetDirectoryName(executablePath);
            if (string.IsNullOrWhiteSpace(executableDirectory))
            {
                return null;
            }

            var expanded = Environment.ExpandEnvironmentVariables(value.Trim());
            expanded = expanded.Replace(
                ExecutablePathToken,
                executableDirectory,
                StringComparison.OrdinalIgnoreCase);
            expanded = expanded.Replace(
                SystemWriteDataPathToken,
                defaultSystemUserDataPath,
                StringComparison.OrdinalIgnoreCase);

            if (expanded.Contains("__PATH__", StringComparison.OrdinalIgnoreCase))
            {
                // Unknown Factorio path token: do not guess a filesystem location.
                return null;
            }

            if (string.Equals(expanded, "~", StringComparison.Ordinal))
            {
                expanded = userProfilePath;
            }
            else if (expanded.StartsWith("~/", StringComparison.Ordinal) ||
                     expanded.StartsWith("~\\", StringComparison.Ordinal))
            {
                expanded = Path.Combine(userProfilePath, expanded[2..]);
            }

            return Path.GetFullPath(
                Path.IsPathRooted(expanded)
                    ? expanded
                    : Path.Combine(relativeBasePath, expanded));
        }
        catch (ArgumentException)
        {
            return null;
        }
        catch (NotSupportedException)
        {
            return null;
        }
    }

    private static string TrimMatchingQuotes(string value)
    {
        if (value.Length >= 2 &&
            ((value[0] == '"' && value[^1] == '"') ||
             (value[0] == '\'' && value[^1] == '\'')))
        {
            return value[1..^1].Trim();
        }

        return value;
    }

    private static bool IsComment(string line)
        => line.StartsWith(';') || line.StartsWith('#');
}