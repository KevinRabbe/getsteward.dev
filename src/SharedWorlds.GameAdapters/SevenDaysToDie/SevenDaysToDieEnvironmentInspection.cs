using System.Text.RegularExpressions;
using SharedWorlds.Core.Abstractions;
using SharedWorlds.Core.Environment;

namespace SharedWorlds.GameAdapters.SevenDaysToDie;

internal static partial class SevenDaysToDieEnvironment
{
    public static EnvironmentManifest Inspect(GameInstallation installation)
    {
        ArgumentNullException.ThrowIfNull(installation);
        var buildId = ReadRequiredDedicatedServerBuildId(installation);
        var mods = ReadDedicatedServerMods(installation);
        return new EnvironmentManifest(
            SchemaVersion: 1,
            AdapterId: "7-days-to-die",
            GameVersion: buildId,
            Components: mods,
            Configuration: new Dictionary<string, string>(StringComparer.Ordinal));
    }

    internal static string ReadRequiredDedicatedServerBuildId(GameInstallation installation)
    {
        ArgumentNullException.ThrowIfNull(installation);
        if (installation.Metadata is null ||
            !installation.Metadata.TryGetValue(
                SevenDaysToDieInstallationDiscovery.DedicatedServerManifestPathKey,
                out var manifestPath) ||
            string.IsNullOrWhiteSpace(manifestPath))
        {
            throw new InvalidOperationException(
                "7 Days to Die Dedicated Server is not installed with a discoverable Steam manifest on this device.");
        }

        var fullManifestPath = Path.GetFullPath(manifestPath);
        RequireRegularFile(
            fullManifestPath,
            "7 Days to Die Dedicated Server Steam manifest");

        string text;
        try
        {
            text = File.ReadAllText(fullManifestPath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException(
                $"7 Days to Die Dedicated Server Steam manifest could not be read: {exception.Message}",
                exception);
        }

        var match = SteamBuildIdRegex().Match(text);
        if (!match.Success || string.IsNullOrWhiteSpace(match.Groups[1].Value))
        {
            throw new InvalidOperationException(
                $"Steam buildid was not found in 7 Days to Die Dedicated Server manifest: {fullManifestPath}");
        }

        return match.Groups[1].Value;
    }

    private static string GetRequiredDedicatedServerRoot(GameInstallation installation)
    {
        if (installation.Metadata is null ||
            !installation.Metadata.TryGetValue(
                SevenDaysToDieInstallationDiscovery.DedicatedServerRootPathKey,
                out var serverRoot) ||
            string.IsNullOrWhiteSpace(serverRoot))
        {
            throw new InvalidOperationException(
                "7 Days to Die Dedicated Server root is not available on this device.");
        }

        var fullRoot = Path.GetFullPath(serverRoot);
        RequireRegularDirectory(
            fullRoot,
            "7 Days to Die Dedicated Server root");
        return fullRoot;
    }

    private static bool TryRequireRegularFile(string path, string description)
        => TryRequireRegularPath(path, description, expectDirectory: false);

    private static bool TryRequireRegularDirectory(string path, string description)
        => TryRequireRegularPath(path, description, expectDirectory: true);

    private static void RequireRegularFile(string path, string description)
    {
        if (!TryRequireRegularFile(path, description))
        {
            throw new InvalidOperationException($"{description} was not found: {path}");
        }
    }

    private static void RequireRegularDirectory(string path, string description)
    {
        if (!TryRequireRegularDirectory(path, description))
        {
            throw new InvalidOperationException($"{description} was not found: {path}");
        }
    }

    private static bool TryRequireRegularPath(
        string path,
        string description,
        bool expectDirectory)
    {
        FileAttributes attributes;
        try
        {
            attributes = File.GetAttributes(path);
        }
        catch (Exception exception) when (
            exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return false;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException(
                $"{description} could not be inspected safely: {path}: {exception.Message}",
                exception);
        }

        if ((attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidOperationException(
                $"{description} is linked or a reparse point. Steward will not use bytes outside the 7 Days to Die environment: {path}");
        }

        var isDirectory = (attributes & FileAttributes.Directory) != 0;
        if (isDirectory != expectDirectory)
        {
            throw new InvalidOperationException(
                $"{description} is not a {(expectDirectory ? "directory" : "regular file")}: {path}");
        }

        return true;
    }

    [GeneratedRegex("\"buildid\"\\s+\"([^\"]+)\"", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SteamBuildIdRegex();
}
