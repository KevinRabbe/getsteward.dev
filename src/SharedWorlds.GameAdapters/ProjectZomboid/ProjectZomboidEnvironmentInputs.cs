using System.Text.RegularExpressions;
using SharedWorlds.Core.Abstractions;

namespace SharedWorlds.GameAdapters.ProjectZomboid;

internal static partial class ProjectZomboidEnvironment
{
    internal static string ReadRequiredDedicatedServerBuildId(GameInstallation installation)
    {
        ArgumentNullException.ThrowIfNull(installation);
        if (installation.Metadata is null ||
            !installation.Metadata.TryGetValue(
                ProjectZomboidInstallationDiscovery.DedicatedServerManifestPathKey,
                out var manifestPath) ||
            string.IsNullOrWhiteSpace(manifestPath))
        {
            throw new InvalidOperationException(
                "Project Zomboid Dedicated Server is not installed with a discoverable Steam manifest on this device.");
        }

        var fullManifestPath = Path.GetFullPath(manifestPath);
        RequireRegularFile(
            fullManifestPath,
            "Project Zomboid Dedicated Server Steam manifest");

        string text;
        try
        {
            text = File.ReadAllText(fullManifestPath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException(
                $"Project Zomboid Dedicated Server Steam manifest could not be read: {exception.Message}",
                exception);
        }

        var match = SteamBuildIdRegex().Match(text);
        if (!match.Success || string.IsNullOrWhiteSpace(match.Groups[1].Value))
        {
            throw new InvalidOperationException(
                $"Steam buildid was not found in Project Zomboid Dedicated Server manifest: {fullManifestPath}");
        }

        return match.Groups[1].Value;
    }

    private static string GetRequiredDedicatedServerRoot(GameInstallation installation)
    {
        if (installation.Metadata is null ||
            !installation.Metadata.TryGetValue(
                ProjectZomboidInstallationDiscovery.DedicatedServerRootPathKey,
                out var serverRoot) ||
            string.IsNullOrWhiteSpace(serverRoot))
        {
            throw new InvalidOperationException(
                "Project Zomboid Dedicated Server root is not available on this device.");
        }

        var fullRoot = Path.GetFullPath(serverRoot);
        RequireRegularDirectory(
            fullRoot,
            "Project Zomboid Dedicated Server root");
        return fullRoot;
    }

    private static string GetRequiredUserDataRoot(GameInstallation installation)
    {
        if (installation.Metadata is null ||
            !installation.Metadata.TryGetValue(
                ProjectZomboidInstallationDiscovery.UserDataPathKey,
                out var userDataRoot) ||
            string.IsNullOrWhiteSpace(userDataRoot))
        {
            throw new InvalidOperationException(
                "Project Zomboid installation is missing its user-data path.");
        }

        var fullRoot = Path.GetFullPath(userDataRoot);
        RequireRegularDirectory(
            fullRoot,
            "Project Zomboid user-data root");
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
                $"{description} is linked or a reparse point. Steward will not use bytes outside the Project Zomboid environment: {path}");
        }

        var isDirectory = (attributes & FileAttributes.Directory) != 0;
        if (isDirectory != expectDirectory)
        {
            throw new InvalidOperationException(
                $"{description} is not a {(expectDirectory ? "directory" : "regular file")}: {path}");
        }

        return true;
    }

    private static string GetServerName(string worldPath)
    {
        var serverName = Path.GetFileName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(worldPath)));
        if (string.IsNullOrWhiteSpace(serverName) ||
            serverName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            throw new InvalidOperationException(
                $"Could not determine a safe Project Zomboid server name from '{worldPath}'.");
        }

        return serverName;
    }

    [GeneratedRegex("\\\"buildid\\\"\\s+\\\"([^\\\"]+)\\\"", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SteamBuildIdRegex();
}
