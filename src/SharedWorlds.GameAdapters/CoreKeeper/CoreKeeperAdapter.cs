using System.Globalization;
using System.Runtime.Versioning;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using SharedWorlds.Core.Abstractions;
using SharedWorlds.Core.Environment;

namespace SharedWorlds.GameAdapters.CoreKeeper;

public sealed class CoreKeeperAdapter : IGameAdapter
{
    public string Id => "core-keeper";
    public string DisplayName => "Core Keeper";
    public GameAdapterCapabilities Capabilities => GameAdapterCapabilities.ExactGameVersion;

    public Task<IReadOnlyList<GameInstallation>> DiscoverInstallationsAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(CoreKeeperInstallationDiscovery.Discover());
    }

    public Task<IReadOnlyList<DetectedWorld>> DiscoverWorldsAsync(
        GameInstallation installation,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(CoreKeeperWorldDiscovery.Discover(installation));
    }

    public Task<EnvironmentManifest> InspectEnvironmentAsync(
        GameInstallation installation,
        DetectedWorld world,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(world);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(CoreKeeperEnvironment.Inspect(installation));
    }

    public Task<EnvironmentVerificationReport> VerifyEnvironmentAsync(
        GameInstallation installation,
        EnvironmentManifest requiredEnvironment,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(CoreKeeperEnvironment.Verify(installation, requiredEnvironment));
    }

    public Task<CapturedState> CaptureDetectedWorldAsync(
        GameInstallation installation,
        DetectedWorld world,
        CancellationToken cancellationToken = default)
        => CoreKeeperWorldState.CaptureDetectedWorldAsync(world, cancellationToken);

    public Task<PreparedWorld> PrepareEnvironmentAsync(
        GameInstallation installation,
        EnvironmentManifest requiredEnvironment,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(CoreKeeperWorldState.PrepareEnvironment(installation, requiredEnvironment));
    }

    public Task<CapturedState> CaptureStateAsync(
        PreparedWorld world,
        CancellationToken cancellationToken = default)
        => CoreKeeperWorldState.CapturePreparedWorldAsync(world, cancellationToken);

    public Task RestoreStateAsync(
        PreparedWorld world,
        StatePackage state,
        CancellationToken cancellationToken = default)
        => CoreKeeperWorldState.RestorePreparedWorldAsync(world, state, cancellationToken);

    public Task FinalizePreparedWorldAsync(
        PreparedWorld world,
        PreparedWorldDisposition disposition,
        CancellationToken cancellationToken = default)
        => CoreKeeperWorldState.FinalizePreparedWorldAsync(world, disposition, cancellationToken);
}

internal static partial class CoreKeeperInstallationDiscovery
{
    internal const string ClientExecutablePathKey = "clientExecutablePath";
    internal const string SteamManifestPathKey = "steamManifestPath";
    internal const string SaveProfilesRootPathKey = "saveProfilesRootPath";
    internal const string WorkshopContentRootPathKey = "workshopContentRootPath";
    internal const string ManualModsRootPathKey = "manualModsRootPath";
    internal const string GameSteamAppIdKey = "gameSteamAppId";
    internal const string GameSteamAppId = "1621690";

    public static IReadOnlyList<GameInstallation> Discover()
    {
        if (!OperatingSystem.IsWindows())
        {
            return [];
        }

        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrWhiteSpace(userProfile))
        {
            return [];
        }

        var saveProfilesRoot = Path.Combine(
            userProfile,
            "AppData",
            "LocalLow",
            "Pugstorm",
            "Core Keeper",
            "Steam");
        return DiscoverFromSteamLibraries(DiscoverSteamLibraries(), saveProfilesRoot);
    }

    internal static IReadOnlyList<GameInstallation> DiscoverFromSteamLibraries(
        IEnumerable<string> steamLibraries,
        string saveProfilesRoot)
    {
        ArgumentNullException.ThrowIfNull(steamLibraries);
        ArgumentException.ThrowIfNullOrWhiteSpace(saveProfilesRoot);

        var comparer = OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;
        var installations = new List<GameInstallation>();
        var seenRoots = new HashSet<string>(comparer);

        foreach (var rawLibrary in steamLibraries)
        {
            var library = NormalizePathOrNull(rawLibrary);
            if (library is null)
            {
                continue;
            }

            var root = NormalizePathOrNull(Path.Combine(
                library,
                "steamapps",
                "common",
                "Core Keeper"));
            if (root is null || !seenRoots.Add(root))
            {
                continue;
            }

            var executable = Path.Combine(root, "CoreKeeper.exe");
            if (!File.Exists(executable))
            {
                continue;
            }

            var metadata = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [ClientExecutablePathKey] = Path.GetFullPath(executable),
                [SaveProfilesRootPathKey] = Path.GetFullPath(saveProfilesRoot),
                [WorkshopContentRootPathKey] = Path.GetFullPath(Path.Combine(
                    library,
                    "steamapps",
                    "workshop",
                    "content",
                    GameSteamAppId)),
                [ManualModsRootPathKey] = Path.GetFullPath(Path.Combine(
                    root,
                    "CoreKeeper_Data",
                    "StreamingAssets",
                    "Mods")),
                [GameSteamAppIdKey] = GameSteamAppId
            };

            var manifestPath = Path.Combine(
                library,
                "steamapps",
                $"appmanifest_{GameSteamAppId}.acf");
            if (File.Exists(manifestPath))
            {
                metadata[SteamManifestPathKey] = Path.GetFullPath(manifestPath);
            }

            installations.Add(new GameInstallation(
                Id: $"core-keeper:{root}",
                RootPath: root,
                Source: "steam",
                Metadata: metadata));
        }

        return installations;
    }

    [SupportedOSPlatform("windows")]
    private static IEnumerable<string> DiscoverSteamLibraries()
    {
        var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in DiscoverWindowsSteamRoots())
        {
            var normalized = NormalizePathOrNull(root);
            if (normalized is not null)
            {
                roots.Add(normalized);
            }
        }

        var libraries = new HashSet<string>(roots, StringComparer.OrdinalIgnoreCase);
        foreach (var steamRoot in roots)
        {
            var libraryFoldersPath = Path.Combine(steamRoot, "steamapps", "libraryfolders.vdf");
            if (!File.Exists(libraryFoldersPath))
            {
                continue;
            }

            try
            {
                var text = File.ReadAllText(libraryFoldersPath);
                foreach (Match match in SteamLibraryPathRegex().Matches(text))
                {
                    var value = match.Groups[1].Value.Replace("\\\\", "\\", StringComparison.Ordinal);
                    var normalized = NormalizePathOrNull(value);
                    if (normalized is not null)
                    {
                        libraries.Add(normalized);
                    }
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Steam discovery is best-effort.
            }
        }

        return libraries;
    }

    [SupportedOSPlatform("windows")]
    private static IEnumerable<string> DiscoverWindowsSteamRoots()
    {
        var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        if (!string.IsNullOrWhiteSpace(programFilesX86))
        {
            roots.Add(Path.Combine(programFilesX86, "Steam"));
        }

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
            if (key?.GetValue("SteamPath") is string steamPath && !string.IsNullOrWhiteSpace(steamPath))
            {
                roots.Add(steamPath);
            }
        }
        catch
        {
            // Registry discovery is optional.
        }

        return roots;
    }

    private static string? NormalizePathOrNull(string path)
    {
        try
        {
            return Path.GetFullPath(path);
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    [GeneratedRegex("\\\"path\\\"\\s+\\\"([^\\\"]+)\\\"", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SteamLibraryPathRegex();
}

internal static class CoreKeeperWorldDiscovery
{
    private const string WorldSuffix = ".world.gzip";

    public static IReadOnlyList<DetectedWorld> Discover(GameInstallation installation)
    {
        ArgumentNullException.ThrowIfNull(installation);
        if (installation.Metadata is null ||
            !installation.Metadata.TryGetValue(CoreKeeperInstallationDiscovery.SaveProfilesRootPathKey, out var profilesRoot) ||
            string.IsNullOrWhiteSpace(profilesRoot))
        {
            return [];
        }

        return DiscoverFromProfilesRoot(profilesRoot);
    }

    internal static IReadOnlyList<DetectedWorld> DiscoverFromProfilesRoot(string profilesRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profilesRoot);
        var fullRoot = Path.GetFullPath(profilesRoot);
        if (!IsRegularDirectory(fullRoot))
        {
            return [];
        }

        var worlds = new List<DetectedWorld>();
        try
        {
            foreach (var profile in Directory.EnumerateDirectories(fullRoot, "*", SearchOption.TopDirectoryOnly))
            {
                if (!IsRegularDirectory(profile))
                {
                    continue;
                }

                var profileId = Path.GetFileName(profile);
                var worldsRoot = Path.Combine(profile, "worlds");
                if (!IsRegularDirectory(worldsRoot))
                {
                    continue;
                }

                foreach (var worldPath in Directory.EnumerateFiles(worldsRoot, $"*{WorldSuffix}", SearchOption.TopDirectoryOnly))
                {
                    if (!IsRegularFile(worldPath))
                    {
                        continue;
                    }

                    var fileName = Path.GetFileName(worldPath);
                    var slot = fileName[..^WorldSuffix.Length];
                    if (!int.TryParse(slot, NumberStyles.None, CultureInfo.InvariantCulture, out var slotNumber) ||
                        slotNumber < 0 ||
                        !string.Equals(
                            slotNumber.ToString(CultureInfo.InvariantCulture),
                            slot,
                            StringComparison.Ordinal))
                    {
                        continue;
                    }

                    var infoPath = Path.Combine(profile, "worldinfos", $"{slot}.worldinfo");
                    var generationPath = Path.Combine(profile, "worldgenparams", $"{slot}.json");
                    if (!IsRegularFile(infoPath) || !IsRegularFile(generationPath))
                    {
                        continue;
                    }

                    worlds.Add(new DetectedWorld(
                        Id: $"local:{profileId}:{slot}",
                        DisplayName: $"Core Keeper World {slotNumber + 1}",
                        SourcePath: Path.GetFullPath(worldPath)));
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return [];
        }

        return worlds
            .OrderByDescending(world => GetLastWriteTimeUtcSafe(world.SourcePath))
            .ThenBy(world => world.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    internal static bool IsRegularDirectory(string path)
        => TryGetRegularAttributes(path, expectDirectory: true);

    internal static bool IsRegularFile(string path)
        => TryGetRegularAttributes(path, expectDirectory: false);

    private static bool TryGetRegularAttributes(string path, bool expectDirectory)
    {
        try
        {
            var attributes = File.GetAttributes(path);
            return (attributes & FileAttributes.ReparsePoint) == 0 &&
                   ((attributes & FileAttributes.Directory) != 0) == expectDirectory;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static DateTime GetLastWriteTimeUtcSafe(string path)
    {
        try
        {
            return File.GetLastWriteTimeUtc(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return DateTime.MinValue;
        }
    }
}

internal static partial class CoreKeeperEnvironment
{
    internal const long MaximumSteamManifestBytes = 4L * 1024 * 1024;

    public static EnvironmentManifest Inspect(GameInstallation installation)
    {
        RequireVanillaInstallation(installation);
        return new EnvironmentManifest(
            SchemaVersion: 1,
            AdapterId: "core-keeper",
            GameVersion: ReadRequiredBuildId(installation),
            Components: [],
            Configuration: new Dictionary<string, string>(StringComparer.Ordinal));
    }

    public static EnvironmentVerificationReport Verify(
        GameInstallation installation,
        EnvironmentManifest requiredEnvironment)
    {
        ArgumentNullException.ThrowIfNull(installation);
        ArgumentNullException.ThrowIfNull(requiredEnvironment);
        var issues = new List<EnvironmentVerificationIssue>();

        if (requiredEnvironment.SchemaVersion != 1)
        {
            issues.Add(new EnvironmentVerificationIssue(
                "core-keeper-environment-schema-unsupported",
                $"Core Keeper environment schema {requiredEnvironment.SchemaVersion} is not supported."));
        }

        if (!string.Equals(requiredEnvironment.AdapterId, "core-keeper", StringComparison.Ordinal))
        {
            issues.Add(new EnvironmentVerificationIssue(
                "core-keeper-adapter-mismatch",
                $"The required environment belongs to adapter '{requiredEnvironment.AdapterId}', not Core Keeper."));
        }

        if (requiredEnvironment.Components.Count != 0)
        {
            issues.Add(new EnvironmentVerificationIssue(
                "core-keeper-components-unsupported",
                "The current Core Keeper adapter supports only vanilla Worlds and no adapter-managed mod components."));
        }

        try
        {
            RequireVanillaInstallation(installation);
            var installedBuild = ReadRequiredBuildId(installation);
            if (!string.Equals(installedBuild, requiredEnvironment.GameVersion, StringComparison.Ordinal))
            {
                issues.Add(new EnvironmentVerificationIssue(
                    "core-keeper-version-mismatch",
                    $"This World requires Core Keeper Steam build {requiredEnvironment.GameVersion}, but this device has build {installedBuild}."));
            }
        }
        catch (InvalidOperationException exception)
        {
            issues.Add(new EnvironmentVerificationIssue("core-keeper-environment-unavailable", exception.Message));
        }

        return issues.Count == 0
            ? EnvironmentVerificationReport.Ready()
            : EnvironmentVerificationReport.Blocked(issues.ToArray());
    }

    internal static void RequireCompatible(
        GameInstallation installation,
        EnvironmentManifest requiredEnvironment)
    {
        ArgumentNullException.ThrowIfNull(requiredEnvironment);
        if (requiredEnvironment.SchemaVersion != 1 ||
            !string.Equals(requiredEnvironment.AdapterId, "core-keeper", StringComparison.Ordinal) ||
            requiredEnvironment.Components.Count != 0)
        {
            throw new InvalidOperationException(
                "The required environment is not a supported vanilla Core Keeper environment revision.");
        }

        RequireVanillaInstallation(installation);
        var installedBuild = ReadRequiredBuildId(installation);
        if (!string.Equals(installedBuild, requiredEnvironment.GameVersion, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Core Keeper Steam build {installedBuild} does not match required build {requiredEnvironment.GameVersion}.");
        }
    }

    internal static void RequireVanillaInstallation(GameInstallation installation)
    {
        ArgumentNullException.ThrowIfNull(installation);
        if (installation.Metadata is null)
        {
            throw new InvalidOperationException("Core Keeper environment metadata is unavailable.");
        }

        RequireEmptyOptionalDirectory(
            installation.Metadata,
            CoreKeeperInstallationDiscovery.ManualModsRootPathKey,
            "Core Keeper's manual Mods directory");
        RequireEmptyOptionalDirectory(
            installation.Metadata,
            CoreKeeperInstallationDiscovery.WorkshopContentRootPathKey,
            "Core Keeper's Steam Workshop content directory");

        if (!installation.Metadata.TryGetValue(
                CoreKeeperInstallationDiscovery.SaveProfilesRootPathKey,
                out var profilesRoot) ||
            string.IsNullOrWhiteSpace(profilesRoot))
        {
            throw new InvalidOperationException(
                "Core Keeper's local Steam save-profile root is unavailable, so Steward cannot prove a vanilla environment.");
        }

        var fullProfilesRoot = Path.GetFullPath(profilesRoot);
        if (!Directory.Exists(fullProfilesRoot))
        {
            return;
        }

        try
        {
            if ((File.GetAttributes(fullProfilesRoot) & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidOperationException(
                    "Core Keeper's Steam save-profile root is linked. Steward's current adapter refuses redirected mod state.");
            }

            foreach (var profile in Directory.EnumerateDirectories(fullProfilesRoot, "*", SearchOption.TopDirectoryOnly))
            {
                if (!CoreKeeperWorldDiscovery.IsRegularDirectory(profile))
                {
                    throw new InvalidOperationException(
                        "Core Keeper has a linked or unreadable Steam save profile. Steward's current adapter cannot prove a vanilla environment.");
                }

                var modsPath = Path.Combine(profile, "mods");
                if (!Directory.Exists(modsPath))
                {
                    continue;
                }

                if (!CoreKeeperWorldDiscovery.IsRegularDirectory(modsPath) ||
                    Directory.EnumerateFileSystemEntries(modsPath).Any())
                {
                    throw new InvalidOperationException(
                        "Core Keeper has a linked or non-empty user Mods directory. Steward's current Core Keeper adapter is vanilla-only.");
                }
            }
        }
        catch (InvalidOperationException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException(
                $"Steward could not safely inspect Core Keeper's save-profile mod state: {fullProfilesRoot}",
                exception);
        }
    }

    private static void RequireEmptyOptionalDirectory(
        IReadOnlyDictionary<string, string> metadata,
        string key,
        string description)
    {
        if (!metadata.TryGetValue(key, out var path) || string.IsNullOrWhiteSpace(path))
        {
            throw new InvalidOperationException($"{description} path is unavailable.");
        }

        var fullPath = Path.GetFullPath(path);
        if (!Directory.Exists(fullPath))
        {
            return;
        }

        try
        {
            if (!CoreKeeperWorldDiscovery.IsRegularDirectory(fullPath) ||
                Directory.EnumerateFileSystemEntries(fullPath).Any())
            {
                throw new InvalidOperationException(
                    $"{description} is linked or non-empty. Steward's current Core Keeper adapter is vanilla-only.");
            }
        }
        catch (InvalidOperationException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException(
                $"Steward could not safely inspect {description}: {fullPath}",
                exception);
        }
    }

    internal static string ReadRequiredBuildId(GameInstallation installation)
    {
        ArgumentNullException.ThrowIfNull(installation);
        if (installation.Metadata is null ||
            !installation.Metadata.TryGetValue(CoreKeeperInstallationDiscovery.SteamManifestPathKey, out var manifestPath) ||
            string.IsNullOrWhiteSpace(manifestPath))
        {
            throw new InvalidOperationException(
                "Core Keeper's Steam manifest is unavailable, so Steward cannot verify an exact game build.");
        }

        var fullPath = Path.GetFullPath(manifestPath);
        using var stream = OpenOwnedManifest(fullPath);
        var maximum = checked((int)MaximumSteamManifestBytes);
        var bytes = new byte[maximum + 1];
        var total = 0;
        while (total < bytes.Length)
        {
            var read = stream.Read(bytes, total, bytes.Length - total);
            if (read == 0)
            {
                break;
            }

            total += read;
        }

        if (total > maximum)
        {
            throw new InvalidOperationException(
                $"Core Keeper's Steam manifest exceeded Steward's {MaximumSteamManifestBytes}-byte metadata safety limit while being read: {fullPath}");
        }

        var text = Encoding.UTF8.GetString(bytes, 0, total);
        var match = SteamBuildIdRegex().Match(text);
        if (!match.Success || string.IsNullOrWhiteSpace(match.Groups[1].Value))
        {
            throw new InvalidOperationException(
                $"Steam buildid was not found in Core Keeper's manifest: {fullPath}");
        }

        return match.Groups[1].Value;
    }

    private static FileStream OpenOwnedManifest(string fullPath)
    {
        FileStream stream;
        try
        {
            stream = new FileStream(
                fullPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 64 * 1024,
                useAsync: false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException(
                $"Core Keeper's Steam manifest could not be opened safely: {fullPath}",
                exception);
        }

        try
        {
            var attributes = File.GetAttributes(fullPath);
            if ((attributes & FileAttributes.ReparsePoint) != 0 ||
                (attributes & FileAttributes.Directory) != 0)
            {
                throw new InvalidOperationException(
                    $"Core Keeper's Steam manifest is not a regular owned file: {fullPath}");
            }

            if (stream.Length > MaximumSteamManifestBytes)
            {
                throw new InvalidOperationException(
                    $"Core Keeper's Steam manifest exceeds Steward's {MaximumSteamManifestBytes}-byte metadata safety limit: {fullPath}");
            }

            return stream;
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    [GeneratedRegex("\\\"buildid\\\"\\s+\\\"([^\\\"]+)\\\"", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SteamBuildIdRegex();
}
