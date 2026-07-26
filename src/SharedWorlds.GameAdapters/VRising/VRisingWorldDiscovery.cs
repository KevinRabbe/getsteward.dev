using System.Globalization;
using System.Text.RegularExpressions;
using SharedWorlds.Core.Abstractions;

namespace SharedWorlds.GameAdapters.VRising;

internal static partial class VRisingWorldDiscovery
{
    internal const string ServerGameSettingsFileName = "ServerGameSettings.json";
    internal const string ServerHostSettingsFileName = "ServerHostSettings.json";
    internal const string SessionIdFileName = "SessionId.json";
    internal const string StartDateFileName = "StartDate.json";

    public static IReadOnlyList<DetectedWorld> Discover(GameInstallation installation)
    {
        ArgumentNullException.ThrowIfNull(installation);
        if (installation.Metadata is null ||
            !installation.Metadata.TryGetValue(
                VRisingInstallationDiscovery.SaveVersionRootPathKey,
                out var saveVersionRoot) ||
            string.IsNullOrWhiteSpace(saveVersionRoot))
        {
            return [];
        }

        return DiscoverFromSaveVersionRoot(saveVersionRoot);
    }

    internal static IReadOnlyList<DetectedWorld> DiscoverFromSaveVersionRoot(string saveVersionRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(saveVersionRoot);
        var root = Path.GetFullPath(saveVersionRoot);
        if (!IsRegularDirectory(root))
        {
            return [];
        }

        var worlds = new List<(DetectedWorld World, DateTime LatestWriteUtc)>();
        try
        {
            foreach (var session in Directory.EnumerateDirectories(
                         root,
                         "*",
                         SearchOption.TopDirectoryOnly))
            {
                var sessionName = Path.GetFileName(Path.TrimEndingDirectorySeparator(session));
                if (!Guid.TryParseExact(sessionName, "D", out _))
                {
                    continue;
                }

                VRisingNativeBundle bundle;
                try
                {
                    bundle = ResolveCurrentBundle(session);
                }
                catch (InvalidOperationException)
                {
                    continue;
                }

                worlds.Add((
                    new DetectedWorld(
                        $"local:{sessionName}",
                        sessionName,
                        Path.GetFullPath(session)),
                    GetLastWriteTimeUtcSafe(bundle.AutoSavePath)));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }

        return worlds
            .OrderByDescending(item => item.LatestWriteUtc)
            .ThenBy(item => item.World.DisplayName, StringComparer.OrdinalIgnoreCase)
            .Select(item => item.World)
            .ToArray();
    }

    internal static VRisingNativeBundle ResolveCurrentBundle(string sessionPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionPath);
        var fullSession = Path.GetFullPath(sessionPath);
        RequireRegularDirectory(fullSession, "V Rising v4 session");

        var sessionName = Path.GetFileName(Path.TrimEndingDirectorySeparator(fullSession));
        if (!Guid.TryParseExact(sessionName, "D", out _))
        {
            throw new InvalidOperationException(
                $"V Rising local session directory is not a canonical GUID identity: {fullSession}");
        }

        var gameSettings = Path.Combine(fullSession, ServerGameSettingsFileName);
        var sessionId = Path.Combine(fullSession, SessionIdFileName);
        var startDate = Path.Combine(fullSession, StartDateFileName);
        RequireRegularNonEmptyFile(gameSettings, "V Rising World game settings");
        RequireRegularNonEmptyFile(sessionId, "V Rising session identity metadata");
        RequireRegularNonEmptyFile(startDate, "V Rising session start metadata");

        var autosaves = new List<(ulong Generation, string Path)>();
        try
        {
            foreach (var file in Directory.EnumerateFiles(
                         fullSession,
                         "*",
                         SearchOption.TopDirectoryOnly))
            {
                var fileName = Path.GetFileName(file);
                var match = AutoSaveRegex().Match(fileName);
                if (match.Success)
                {
                    if (!ulong.TryParse(
                            match.Groups[1].Value,
                            NumberStyles.None,
                            CultureInfo.InvariantCulture,
                            out var generation) ||
                        !IsRegularNonEmptyFile(file))
                    {
                        throw new InvalidOperationException(
                            $"V Rising session contains an invalid autosave generation: {file}");
                    }

                    autosaves.Add((generation, Path.GetFullPath(file)));
                    continue;
                }

                if (string.Equals(fileName, ServerGameSettingsFileName, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(fileName, ServerHostSettingsFileName, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(fileName, SessionIdFileName, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(fileName, StartDateFileName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                throw new InvalidOperationException(
                    $"V Rising v4 session contains an unrecognized top-level file: {file}");
            }

            if (Directory.EnumerateDirectories(fullSession, "*", SearchOption.TopDirectoryOnly).Any())
            {
                throw new InvalidOperationException(
                    $"V Rising v4 session contains an unrecognized nested directory: {fullSession}");
            }
        }
        catch (InvalidOperationException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException(
                $"V Rising v4 session could not be inspected safely: {fullSession}",
                ex);
        }

        if (autosaves.Count == 0)
        {
            throw new InvalidOperationException(
                $"V Rising v4 session has no current autosave: {fullSession}");
        }

        var latestGeneration = autosaves.Max(item => item.Generation);
        var latest = autosaves
            .Where(item => item.Generation == latestGeneration)
            .ToArray();
        if (latest.Length != 1)
        {
            throw new InvalidOperationException(
                $"V Rising v4 session has more than one representation of autosave generation {latestGeneration}: {fullSession}");
        }

        return new VRisingNativeBundle(
            fullSession,
            latestGeneration,
            latest[0].Path,
            Path.GetFullPath(gameSettings),
            Path.GetFullPath(sessionId),
            Path.GetFullPath(startDate));
    }

    internal static bool IsRegularNonEmptyFile(string path)
    {
        try
        {
            var attributes = File.GetAttributes(path);
            return (attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) == 0 &&
                   new FileInfo(path).Length > 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    internal static bool IsRegularDirectory(string path)
    {
        try
        {
            var attributes = File.GetAttributes(path);
            return (attributes & FileAttributes.Directory) != 0 &&
                   (attributes & FileAttributes.ReparsePoint) == 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static void RequireRegularDirectory(string path, string description)
    {
        if (!IsRegularDirectory(path))
        {
            throw new InvalidOperationException(
                $"{description} must be a regular non-linked directory: {path}");
        }
    }

    private static void RequireRegularNonEmptyFile(string path, string description)
    {
        if (!IsRegularNonEmptyFile(path))
        {
            throw new InvalidOperationException(
                $"{description} must be a regular non-linked non-empty file: {path}");
        }
    }

    private static DateTime GetLastWriteTimeUtcSafe(string path)
    {
        try
        {
            return File.GetLastWriteTimeUtc(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return DateTime.MinValue;
        }
    }

    [GeneratedRegex("^AutoSave_([0-9]+)\\.save(?:\\.gz)?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AutoSaveRegex();
}

internal sealed record VRisingNativeBundle(
    string SessionPath,
    ulong AutoSaveGeneration,
    string AutoSavePath,
    string ServerGameSettingsPath,
    string SessionIdPath,
    string StartDatePath);
