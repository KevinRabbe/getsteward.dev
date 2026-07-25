using System.Runtime.Versioning;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using SharedWorlds.Core.Abstractions;
using SharedWorlds.Core.Environment;

namespace SharedWorlds.GameAdapters.Necesse;

public sealed class NecesseAdapter : IGameAdapter
{
    public string Id => "necesse";
    public string DisplayName => "Necesse";
    public GameAdapterCapabilities Capabilities => GameAdapterCapabilities.ExactGameVersion;

    public Task<IReadOnlyList<GameInstallation>> DiscoverInstallationsAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(NecesseInstallationDiscovery.Discover());
    }

    public Task<IReadOnlyList<DetectedWorld>> DiscoverWorldsAsync(
        GameInstallation installation,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(NecesseWorldDiscovery.Discover(installation));
    }

    public Task<EnvironmentManifest> InspectEnvironmentAsync(
        GameInstallation installation,
        DetectedWorld world,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(world);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(NecesseEnvironment.Inspect(installation));
    }

    public Task<EnvironmentVerificationReport> VerifyEnvironmentAsync(
        GameInstallation installation,
        EnvironmentManifest requiredEnvironment,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(NecesseEnvironment.Verify(installation, requiredEnvironment));
    }

    public Task<CapturedState> CaptureDetectedWorldAsync(
        GameInstallation installation,
        DetectedWorld world,
        CancellationToken cancellationToken = default)
        => NecesseWorldState.CaptureDetectedWorldAsync(world, cancellationToken);

    public Task<PreparedWorld> PrepareEnvironmentAsync(
        GameInstallation installation,
        EnvironmentManifest requiredEnvironment,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(NecesseWorldState.PrepareEnvironment(installation, requiredEnvironment));
    }

    public Task<CapturedState> CaptureStateAsync(
        PreparedWorld world,
        CancellationToken cancellationToken = default)
        => NecesseWorldState.CapturePreparedWorldAsync(world, cancellationToken);

    public Task RestoreStateAsync(
        PreparedWorld world,
        StatePackage state,
        CancellationToken cancellationToken = default)
        => NecesseWorldState.RestorePreparedWorldAsync(world, state, cancellationToken);

    public Task FinalizePreparedWorldAsync(
        PreparedWorld world,
        PreparedWorldDisposition disposition,
        CancellationToken cancellationToken = default)
        => NecesseWorldState.FinalizePreparedWorldAsync(world, disposition, cancellationToken);
}

internal static partial class NecesseInstallationDiscovery
{
    internal const string ClientExecutablePathKey = "clientExecutablePath";
    internal const string SteamManifestPathKey = "steamManifestPath";
    internal const string WorldRootPathKey = "worldRootPath";
    internal const string ModsRootPathKey = "modsRootPath";
    internal const string GameSteamAppIdKey = "gameSteamAppId";
    internal const string GameSteamAppId = "1169040";

    public static IReadOnlyList<GameInstallation> Discover()
    {
        if (!OperatingSystem.IsWindows())
        {
            return [];
        }

        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (string.IsNullOrWhiteSpace(appData))
        {
            return [];
        }

        var necesseDataRoot = Path.Combine(appData, "Necesse");
        var worldRoot = Path.Combine(necesseDataRoot, "saves", "worlds");
        var modsRoot = Path.Combine(necesseDataRoot, "mods");
        return DiscoverFromSteamLibraries(DiscoverSteamLibraries(), worldRoot, modsRoot);
    }

    internal static IReadOnlyList<GameInstallation> DiscoverFromSteamLibraries(
        IEnumerable<string> steamLibraries,
        string worldRoot,
        string modsRoot)
    {
        ArgumentNullException.ThrowIfNull(steamLibraries);
        ArgumentException.ThrowIfNullOrWhiteSpace(worldRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(modsRoot);

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
                "Necesse"));
            if (root is null || !seenRoots.Add(root))
            {
                continue;
            }

            var executable = Path.Combine(root, "Necesse.exe");
            if (!File.Exists(executable))
            {
                continue;
            }

            var metadata = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [ClientExecutablePathKey] = Path.GetFullPath(executable),
                [WorldRootPathKey] = Path.GetFullPath(worldRoot),
                [ModsRootPathKey] = Path.GetFullPath(modsRoot),
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
                Id: $"necesse:{root}",
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

internal static class NecesseWorldDiscovery
{
    public static IReadOnlyList<DetectedWorld> Discover(GameInstallation installation)
    {
        ArgumentNullException.ThrowIfNull(installation);
        if (installation.Metadata is null ||
            !installation.Metadata.TryGetValue(NecesseInstallationDiscovery.WorldRootPathKey, out var worldRoot) ||
            string.IsNullOrWhiteSpace(worldRoot))
        {
            return [];
        }

        return DiscoverFromWorldRoot(worldRoot);
    }

    internal static IReadOnlyList<DetectedWorld> DiscoverFromWorldRoot(string worldRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(worldRoot);
        var fullRoot = Path.GetFullPath(worldRoot);
        if (!IsRegularDirectory(fullRoot))
        {
            return [];
        }

        try
        {
            return Directory
                .EnumerateFiles(fullRoot, "*.zip", SearchOption.TopDirectoryOnly)
                .Where(IsRegularFile)
                .Select(path => new DetectedWorld(
                    Id: $"local:{Path.GetFileNameWithoutExtension(path)}",
                    DisplayName: Path.GetFileNameWithoutExtension(path),
                    SourcePath: Path.GetFullPath(path)))
                .OrderByDescending(world => GetLastWriteTimeUtcSafe(world.SourcePath))
                .ThenBy(world => world.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static bool IsRegularDirectory(string path)
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

internal static partial class NecesseEnvironment
{
    internal const long MaximumSteamManifestBytes = 4L * 1024 * 1024;

    public static EnvironmentManifest Inspect(GameInstallation installation)
    {
        RequireVanillaInstallation(installation);
        return new EnvironmentManifest(
            SchemaVersion: 1,
            AdapterId: "necesse",
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
                "necesse-environment-schema-unsupported",
                $"Necesse environment schema {requiredEnvironment.SchemaVersion} is not supported."));
        }

        if (!string.Equals(requiredEnvironment.AdapterId, "necesse", StringComparison.Ordinal))
        {
            issues.Add(new EnvironmentVerificationIssue(
                "necesse-adapter-mismatch",
                $"The required environment belongs to adapter '{requiredEnvironment.AdapterId}', not Necesse."));
        }

        if (requiredEnvironment.Components.Count != 0)
        {
            issues.Add(new EnvironmentVerificationIssue(
                "necesse-components-unsupported",
                "The current Necesse adapter supports only vanilla compressed Worlds and no adapter-managed mod components."));
        }

        try
        {
            RequireVanillaInstallation(installation);
            var installedBuild = ReadRequiredBuildId(installation);
            if (!string.Equals(installedBuild, requiredEnvironment.GameVersion, StringComparison.Ordinal))
            {
                issues.Add(new EnvironmentVerificationIssue(
                    "necesse-version-mismatch",
                    $"This World requires Necesse Steam build {requiredEnvironment.GameVersion}, but this device has build {installedBuild}."));
            }
        }
        catch (InvalidOperationException exception)
        {
            issues.Add(new EnvironmentVerificationIssue("necesse-environment-unavailable", exception.Message));
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
            !string.Equals(requiredEnvironment.AdapterId, "necesse", StringComparison.Ordinal) ||
            requiredEnvironment.Components.Count != 0)
        {
            throw new InvalidOperationException(
                "The required environment is not a supported vanilla Necesse environment revision.");
        }

        RequireVanillaInstallation(installation);
        var installedBuild = ReadRequiredBuildId(installation);
        if (!string.Equals(installedBuild, requiredEnvironment.GameVersion, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Necesse Steam build {installedBuild} does not match required build {requiredEnvironment.GameVersion}.");
        }
    }

    internal static void RequireVanillaInstallation(GameInstallation installation)
    {
        ArgumentNullException.ThrowIfNull(installation);
        if (installation.Metadata is null ||
            !installation.Metadata.TryGetValue(NecesseInstallationDiscovery.ModsRootPathKey, out var modsRoot) ||
            string.IsNullOrWhiteSpace(modsRoot))
        {
            throw new InvalidOperationException(
                "Necesse's local mods path is unavailable, so Steward cannot prove that this is a vanilla environment.");
        }

        var fullModsRoot = Path.GetFullPath(modsRoot);
        if (!Directory.Exists(fullModsRoot))
        {
            return;
        }

        try
        {
            var attributes = File.GetAttributes(fullModsRoot);
            if ((attributes & FileAttributes.ReparsePoint) != 0 ||
                (attributes & FileAttributes.Directory) == 0 ||
                Directory.EnumerateFileSystemEntries(fullModsRoot).Any())
            {
                throw new InvalidOperationException(
                    "This Necesse installation has a linked or non-empty local mods directory. Steward's current Necesse adapter is vanilla-only.");
            }
        }
        catch (InvalidOperationException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException(
                $"Steward could not safely inspect Necesse's mods directory: {fullModsRoot}",
                exception);
        }
    }

    internal static string ReadRequiredBuildId(GameInstallation installation)
    {
        ArgumentNullException.ThrowIfNull(installation);
        if (installation.Metadata is null ||
            !installation.Metadata.TryGetValue(NecesseInstallationDiscovery.SteamManifestPathKey, out var manifestPath) ||
            string.IsNullOrWhiteSpace(manifestPath))
        {
            throw new InvalidOperationException(
                "Necesse's Steam manifest is unavailable, so Steward cannot verify an exact game build.");
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
                $"Necesse's Steam manifest exceeded Steward's {MaximumSteamManifestBytes}-byte metadata safety limit while being read: {fullPath}");
        }

        var text = Encoding.UTF8.GetString(bytes, 0, total);
        var match = SteamBuildIdRegex().Match(text);
        if (!match.Success || string.IsNullOrWhiteSpace(match.Groups[1].Value))
        {
            throw new InvalidOperationException(
                $"Steam buildid was not found in Necesse's manifest: {fullPath}");
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
                $"Necesse's Steam manifest could not be opened safely: {fullPath}",
                exception);
        }

        try
        {
            var attributes = File.GetAttributes(fullPath);
            if ((attributes & FileAttributes.ReparsePoint) != 0 ||
                (attributes & FileAttributes.Directory) != 0)
            {
                throw new InvalidOperationException(
                    $"Necesse's Steam manifest is not a regular owned file: {fullPath}");
            }

            if (stream.Length > MaximumSteamManifestBytes)
            {
                throw new InvalidOperationException(
                    $"Necesse's Steam manifest exceeds Steward's {MaximumSteamManifestBytes}-byte metadata safety limit: {fullPath}");
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
