using SharedWorlds.Core.Abstractions;
using SharedWorlds.Core.Environment;

namespace SharedWorlds.GameAdapters.Palworld;

internal static partial class PalworldDedicatedServerHosting
{
    private const string DedicatedProfileId = "0";
    private const string LevelSaveFileName = "Level.sav";

    public static PreparedWorld PrepareEnvironment(
        GameInstallation installation,
        EnvironmentManifest requiredEnvironment)
    {
        ArgumentNullException.ThrowIfNull(installation);
        ArgumentNullException.ThrowIfNull(requiredEnvironment);

        EnsureSupportedPlatform();
        EnsureDedicatedServerAvailable(installation);

        if (!string.Equals(requiredEnvironment.AdapterId, "palworld", StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"Environment belongs to adapter '{requiredEnvironment.AdapterId}', not Palworld.",
                nameof(requiredEnvironment));
        }

        if (requiredEnvironment.Configuration.TryGetValue(HostingModeKey, out var hostingMode) &&
            !string.Equals(hostingMode, DedicatedHostingMode, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Palworld environment hosting mode '{hostingMode}' is not supported by the dedicated-host path.");
        }

        if (!requiredEnvironment.Configuration.TryGetValue(DedicatedServerNameKey, out var worldId) ||
            string.IsNullOrWhiteSpace(worldId))
        {
            throw new InvalidOperationException(
                $"Palworld environment is missing required configuration '{DedicatedServerNameKey}'.");
        }

        ValidateWorldId(worldId);

        var serverRoot = GetRequiredMetadata(
            installation,
            PalworldInstallationDiscovery.DedicatedServerRootPathKey);
        var destinationWorldPath = GetDedicatedWorldPath(serverRoot, worldId);
        Directory.CreateDirectory(Path.GetDirectoryName(destinationWorldPath)!);

        return new PreparedWorld(
            Installation: installation,
            WorkingDirectory: destinationWorldPath,
            Environment: requiredEnvironment);
    }

    public static PreparedWorld PrepareDetectedWorld(
        GameInstallation installation,
        DetectedWorld world)
    {
        ArgumentNullException.ThrowIfNull(installation);
        ArgumentNullException.ThrowIfNull(world);

        EnsureSupportedPlatform();
        EnsureDedicatedServerAvailable(installation);

        var sourceWorldPath = Path.GetFullPath(world.SourcePath);
        if (!Directory.Exists(sourceWorldPath) ||
            !File.Exists(Path.Combine(sourceWorldPath, LevelSaveFileName)))
        {
            throw new InvalidOperationException(
                $"The detected Palworld world is no longer available: {sourceWorldPath}");
        }

        var worldId = GetWorldIdFromPath(sourceWorldPath);
        var environment = CreateDedicatedEnvironment(
            worldId,
            GetDedicatedServerBuildIdOrUnknown(installation));
        var prepared = PrepareEnvironment(installation, environment) with
        {
            DisplayName = world.DisplayName
        };

        var destinationWorldPath = Path.GetFullPath(prepared.WorkingDirectory);
        var pathComparer = OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;

        if (!pathComparer.Equals(sourceWorldPath, destinationWorldPath) &&
            !Directory.Exists(destinationWorldPath))
        {
            CopyDirectoryAtomically(sourceWorldPath, destinationWorldPath);
        }

        if (!File.Exists(Path.Combine(destinationWorldPath, LevelSaveFileName)))
        {
            throw new InvalidOperationException(
                $"The prepared Palworld dedicated world is incomplete: {destinationWorldPath}");
        }

        return prepared;
    }

    private static string GetWorldIdFromPath(string worldPath)
    {
        var fullPath = Path.GetFullPath(worldPath);
        var worldId = Path.GetFileName(Path.TrimEndingDirectorySeparator(fullPath));
        if (string.IsNullOrWhiteSpace(worldId))
        {
            throw new InvalidOperationException(
                $"Could not determine the Palworld world id from: {fullPath}");
        }

        ValidateWorldId(worldId);
        return worldId;
    }

    private static void ValidateWorldId(string worldId)
    {
        if (string.IsNullOrWhiteSpace(worldId) ||
            string.Equals(worldId, ".", StringComparison.Ordinal) ||
            string.Equals(worldId, "..", StringComparison.Ordinal) ||
            Path.IsPathRooted(worldId) ||
            !string.Equals(Path.GetFileName(worldId), worldId, StringComparison.Ordinal) ||
            worldId.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            throw new InvalidOperationException(
                $"Palworld dedicated world id is not a safe directory name: '{worldId}'.");
        }
    }

    private static string GetDedicatedWorldPath(string serverRoot, string worldId)
    {
        ValidateWorldId(worldId);
        return Path.GetFullPath(Path.Combine(
            serverRoot,
            "Pal",
            "Saved",
            "SaveGames",
            DedicatedProfileId,
            worldId));
    }

    private static void EnsureSupportedPlatform()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException(
                "The Palworld dedicated-host path is currently validated only on Windows.");
        }
    }

    private static void EnsureDedicatedServerAvailable(GameInstallation installation)
    {
        var serverRoot = GetRequiredMetadata(
            installation,
            PalworldInstallationDiscovery.DedicatedServerRootPathKey);
        var serverExecutable = GetRequiredMetadata(
            installation,
            PalworldInstallationDiscovery.DedicatedServerExecutablePathKey);

        if (!Directory.Exists(serverRoot) || !File.Exists(serverExecutable))
        {
            throw new InvalidOperationException(
                "The Palworld dedicated server is not installed or is no longer available at the discovered path.");
        }
    }

    private static void CopyDirectoryAtomically(string sourcePath, string destinationPath)
    {
        var stagingPath = destinationPath + ".sharedworlds-staging-" + Guid.NewGuid().ToString("N");
        try
        {
            CopyDirectory(sourcePath, stagingPath);
            Directory.Move(stagingPath, destinationPath);
        }
        catch
        {
            TryDeleteDirectory(stagingPath);
            throw;
        }
    }

    private static void CopyDirectory(string sourcePath, string destinationPath)
    {
        Directory.CreateDirectory(destinationPath);

        foreach (var filePath in Directory.EnumerateFiles(sourcePath))
        {
            var destinationFilePath = Path.Combine(destinationPath, Path.GetFileName(filePath));
            File.Copy(filePath, destinationFilePath);
        }

        foreach (var directoryPath in Directory.EnumerateDirectories(sourcePath))
        {
            var destinationDirectoryPath = Path.Combine(
                destinationPath,
                Path.GetFileName(directoryPath));
            CopyDirectory(directoryPath, destinationDirectoryPath);
        }
    }

    private static string GetRequiredMetadata(GameInstallation installation, string key)
    {
        if (installation.Metadata is null ||
            !installation.Metadata.TryGetValue(key, out var value) ||
            string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException(
                $"Palworld installation metadata is missing required value '{key}'.");
        }

        return value;
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (IOException)
        {
            // Best-effort staging cleanup. The original source is never modified.
        }
        catch (UnauthorizedAccessException)
        {
            // Best-effort staging cleanup. The original source is never modified.
        }
    }
}
