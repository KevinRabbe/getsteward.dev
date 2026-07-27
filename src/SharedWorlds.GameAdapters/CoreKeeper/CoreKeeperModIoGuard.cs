using System.Text.Json;

namespace SharedWorlds.GameAdapters.CoreKeeper;

internal static class CoreKeeperModIoGuard
{
    internal const string ModIoGameId = "5289";
    internal const long MaximumSettingsBytes = 1024L * 1024L;
    internal const int MaximumLocalProfiles = 128;

    public static void RequireNoInstalledModsForCurrentWindowsUser()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var defaultStorageRoot = ResolveDefaultStorageRoot();
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localAppData))
        {
            throw new InvalidOperationException(
                "Windows local application-data storage is unavailable, so Steward cannot inspect Core Keeper's mod.io configuration.");
        }

        RequireNoInstalledMods(
            defaultStorageRoot,
            Path.Combine(localAppData, "mod.io"));
    }

    internal static void RequireNoInstalledMods(
        string defaultStorageRoot,
        string settingsRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(defaultStorageRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(settingsRoot);

        var comparer = OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;
        var storageRoots = new HashSet<string>(comparer)
        {
            NormalizeRequiredAbsolutePath(defaultStorageRoot, "Core Keeper's default mod.io storage root")
        };

        var fullSettingsRoot = Path.GetFullPath(settingsRoot);
        if (Directory.Exists(fullSettingsRoot))
        {
            RequireRegularDirectory(
                fullSettingsRoot,
                "Core Keeper's mod.io settings root");

            AddOverrideIfPresent(
                storageRoots,
                Path.Combine(fullSettingsRoot, "globalsettings.json"),
                "mod.io global storage settings");

            var gameSettingsRoot = Path.Combine(fullSettingsRoot, ModIoGameId);
            if (Directory.Exists(gameSettingsRoot))
            {
                RequireRegularDirectory(
                    gameSettingsRoot,
                    "Core Keeper's mod.io game-settings root");
                AddPerProfileOverrides(storageRoots, gameSettingsRoot);
            }
        }

        foreach (var storageRoot in storageRoots)
        {
            RequireEmptyModsDirectory(storageRoot);
        }
    }

    private static string ResolveDefaultStorageRoot()
    {
        var publicRoot = Environment.GetEnvironmentVariable("PUBLIC");
        if (string.IsNullOrWhiteSpace(publicRoot))
        {
            var publicDocuments = Environment.GetFolderPath(Environment.SpecialFolder.CommonDocuments);
            publicRoot = string.IsNullOrWhiteSpace(publicDocuments)
                ? null
                : Directory.GetParent(Path.GetFullPath(publicDocuments))?.FullName;
        }

        if (string.IsNullOrWhiteSpace(publicRoot))
        {
            throw new InvalidOperationException(
                "Windows public-user storage is unavailable, so Steward cannot inspect Core Keeper's default mod.io content root.");
        }

        return Path.Combine(publicRoot, "mod.io");
    }

    private static void AddPerProfileOverrides(
        ISet<string> storageRoots,
        string gameSettingsRoot)
    {
        try
        {
            var profileCount = 0;
            foreach (var profileDirectory in Directory.EnumerateDirectories(
                         gameSettingsRoot,
                         "*",
                         SearchOption.TopDirectoryOnly))
            {
                profileCount++;
                if (profileCount > MaximumLocalProfiles)
                {
                    throw new InvalidOperationException(
                        $"Core Keeper's mod.io settings contain more than Steward's {MaximumLocalProfiles} local-profile safety limit.");
                }

                RequireRegularDirectory(
                    profileDirectory,
                    "a Core Keeper mod.io local-profile settings directory");
                AddOverrideIfPresent(
                    storageRoots,
                    Path.Combine(profileDirectory, "user.json"),
                    "Core Keeper's mod.io local-profile storage settings");
            }
        }
        catch (InvalidOperationException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException(
                $"Steward could not safely inspect Core Keeper's mod.io local-profile settings: {gameSettingsRoot}",
                exception);
        }
    }

    private static void AddOverrideIfPresent(
        ISet<string> storageRoots,
        string settingsPath,
        string description)
    {
        if (!File.Exists(settingsPath))
        {
            return;
        }

        var overridePath = ReadStorageOverride(settingsPath, description);
        if (overridePath is not null)
        {
            storageRoots.Add(overridePath);
        }
    }

    private static string? ReadStorageOverride(
        string settingsPath,
        string description)
    {
        var fullPath = Path.GetFullPath(settingsPath);
        FileStream stream;
        try
        {
            stream = new FileStream(
                fullPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 16 * 1024,
                useAsync: false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException(
                $"Steward could not open {description} safely: {fullPath}",
                exception);
        }

        using (stream)
        {
            try
            {
                var attributes = File.GetAttributes(fullPath);
                if ((attributes & FileAttributes.ReparsePoint) != 0 ||
                    (attributes & FileAttributes.Directory) != 0)
                {
                    throw new InvalidOperationException(
                        $"{description} is not a regular owned file: {fullPath}");
                }

                if (stream.Length > MaximumSettingsBytes)
                {
                    throw new InvalidOperationException(
                        $"{description} exceeds Steward's {MaximumSettingsBytes}-byte metadata safety limit: {fullPath}");
                }

                using var document = JsonDocument.Parse(
                    stream,
                    new JsonDocumentOptions
                    {
                        AllowTrailingCommas = true,
                        CommentHandling = JsonCommentHandling.Skip,
                        MaxDepth = 32
                    });
                if (document.RootElement.ValueKind != JsonValueKind.Object ||
                    !document.RootElement.TryGetProperty("RootLocalStoragePath", out var rootElement))
                {
                    return null;
                }

                if (rootElement.ValueKind != JsonValueKind.String ||
                    string.IsNullOrWhiteSpace(rootElement.GetString()))
                {
                    throw new InvalidOperationException(
                        $"{description} contains an invalid RootLocalStoragePath value: {fullPath}");
                }

                return NormalizeRequiredAbsolutePath(
                    rootElement.GetString()!,
                    $"the RootLocalStoragePath from {description}");
            }
            catch (InvalidOperationException)
            {
                throw;
            }
            catch (JsonException exception)
            {
                throw new InvalidOperationException(
                    $"Steward could not parse {description} safely: {fullPath}",
                    exception);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                throw new InvalidOperationException(
                    $"Steward could not inspect {description} safely: {fullPath}",
                    exception);
            }
        }
    }

    private static string NormalizeRequiredAbsolutePath(
        string path,
        string description)
    {
        try
        {
            if (!Path.IsPathFullyQualified(path))
            {
                throw new InvalidOperationException(
                    $"{description} is not an absolute path.");
            }

            return Path.GetFullPath(path);
        }
        catch (InvalidOperationException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new InvalidOperationException(
                $"{description} is not a safe filesystem path.",
                exception);
        }
    }

    private static void RequireRegularDirectory(
        string path,
        string description)
    {
        try
        {
            var attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.Directory) == 0 ||
                (attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidOperationException(
                    $"{description} is linked or is not a regular directory: {path}");
            }
        }
        catch (InvalidOperationException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException(
                $"Steward could not safely inspect {description}: {path}",
                exception);
        }
    }

    private static void RequireEmptyModsDirectory(string storageRoot)
    {
        var modsPath = Path.Combine(storageRoot, ModIoGameId, "mods");
        if (!Directory.Exists(modsPath))
        {
            return;
        }

        try
        {
            if (!CoreKeeperWorldDiscovery.IsRegularDirectory(modsPath) ||
                Directory.EnumerateFileSystemEntries(modsPath).Any())
            {
                throw new InvalidOperationException(
                    "Core Keeper's official mod.io content directory is linked or non-empty. Steward's current Core Keeper adapter is vanilla-only.");
            }
        }
        catch (InvalidOperationException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException(
                $"Steward could not safely inspect Core Keeper's official mod.io content directory: {modsPath}",
                exception);
        }
    }
}
