using SharedWorlds.Core.Abstractions;

namespace SharedWorlds.GameAdapters.ProjectZomboid;

internal sealed record ProjectZomboidDedicatedServerHostInputs(
    string LaunchPath,
    string WorkingDirectory,
    string CacheDirectory,
    string ServerName,
    IReadOnlyList<string> GameArguments);

internal static partial class ProjectZomboidWorldState
{
    internal static ProjectZomboidDedicatedServerHostInputs CreateDedicatedServerHostInputs(
        PreparedWorld world)
    {
        ArgumentNullException.ThrowIfNull(world);
        ProjectZomboidWorkspaceOwnership.RequireOwned(world.WorkingDirectory);

        var installation = world.Installation;
        RequireDedicatedServerInstalled(installation);
        var serverRoot = GetRequiredInstallationMetadata(
            installation,
            ProjectZomboidInstallationDiscovery.DedicatedServerRootPathKey,
            "dedicated-server root");
        var launchPath = GetRequiredInstallationMetadata(
            installation,
            ProjectZomboidInstallationDiscovery.DedicatedServerLaunchPathKey,
            "dedicated-server launcher");

        var fullServerRoot = Path.GetFullPath(serverRoot);
        var fullLaunchPath = Path.GetFullPath(launchPath);
        RequireRegularDirectory(fullServerRoot, "Project Zomboid Dedicated Server root");
        RequireRegularFile(fullLaunchPath, "Project Zomboid Dedicated Server launcher");
        if (!PathsEqual(Path.GetDirectoryName(fullLaunchPath)!, fullServerRoot))
        {
            throw new InvalidOperationException(
                "Project Zomboid Dedicated Server launcher is outside the discovered dedicated-server root.");
        }

        var cacheDirectory = Path.GetFullPath(world.WorkingDirectory);
        var serverName = ValidateSingleServerBundle(cacheDirectory);
        return new ProjectZomboidDedicatedServerHostInputs(
            fullLaunchPath,
            fullServerRoot,
            cacheDirectory,
            serverName,
            [
                "-cachedir=" + cacheDirectory,
                "-servername",
                serverName
            ]);
    }

    private static void RequireDedicatedServerInstalled(GameInstallation installation)
    {
        ArgumentNullException.ThrowIfNull(installation);
        if (installation.Metadata is null ||
            !installation.Metadata.TryGetValue(
                ProjectZomboidInstallationDiscovery.DedicatedServerInstallStateKey,
                out var state) ||
            !string.Equals(state, "installed", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Project Zomboid Dedicated Server is not installed on this device.");
        }
    }

    private static string GetRequiredInstallationMetadata(
        GameInstallation installation,
        string key,
        string description)
    {
        ArgumentNullException.ThrowIfNull(installation);
        if (installation.Metadata is null ||
            !installation.Metadata.TryGetValue(key, out var value) ||
            string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException(
                $"Project Zomboid installation is missing its {description}.");
        }

        return value;
    }

    private static void RequireRegularDirectory(string path, string description)
    {
        if (!Directory.Exists(path))
        {
            throw new InvalidOperationException($"{description} does not exist: {path}");
        }

        RequireNotReparsePoint(path, description);
    }

    private static void RequireRegularFile(string path, string description)
    {
        if (!File.Exists(path))
        {
            throw new InvalidOperationException($"{description} does not exist: {path}");
        }

        RequireNotReparsePoint(path, description);
    }

    private static void RequireNotReparsePoint(string path, string description)
    {
        FileAttributes attributes;
        try
        {
            attributes = File.GetAttributes(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException(
                $"Steward could not inspect {description} '{path}'.",
                exception);
        }

        if ((attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidOperationException(
                $"{description} is linked or a reparse point: {path}");
        }
    }
}
