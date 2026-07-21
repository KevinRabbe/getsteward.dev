using System.Diagnostics;
using System.Text.RegularExpressions;
using SharedWorlds.Core.Abstractions;
using SharedWorlds.Core.Environment;

namespace SharedWorlds.GameAdapters.Palworld;

internal static partial class PalworldDedicatedServerHosting
{
    private const string DedicatedProfileId = "0";
    private const string LevelSaveFileName = "Level.sav";
    private const string ConfigBackupSuffix = ".sharedworlds-backup";
    private const string HostingModeKey = "hostingMode";
    private const string DedicatedServerNameKey = "dedicatedServerName";
    private const string DedicatedHostingMode = "dedicated-server";

    public static EnvironmentManifest InspectEnvironment(DetectedWorld world)
    {
        ArgumentNullException.ThrowIfNull(world);
        var worldId = GetWorldIdFromPath(world.SourcePath);
        return CreateDedicatedEnvironment(worldId);
    }

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
        var environment = CreateDedicatedEnvironment(worldId);
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

    public static GameSessionHandle Launch(PreparedWorld world)
    {
        ArgumentNullException.ThrowIfNull(world);

        EnsureSupportedPlatform();
        EnsureDedicatedServerAvailable(world.Installation);

        if (!world.Environment.Configuration.TryGetValue(DedicatedServerNameKey, out var worldId) ||
            string.IsNullOrWhiteSpace(worldId))
        {
            throw new InvalidOperationException(
                $"Prepared Palworld environment is missing '{DedicatedServerNameKey}'.");
        }

        ValidateWorldId(worldId);

        var serverRoot = GetRequiredMetadata(
            world.Installation,
            PalworldInstallationDiscovery.DedicatedServerRootPathKey);
        var serverExecutable = GetRequiredMetadata(
            world.Installation,
            PalworldInstallationDiscovery.DedicatedServerExecutablePathKey);
        var expectedWorldPath = GetDedicatedWorldPath(serverRoot, worldId);
        var actualWorldPath = Path.GetFullPath(world.WorkingDirectory);
        var pathComparer = OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;

        if (!pathComparer.Equals(expectedWorldPath, actualWorldPath))
        {
            throw new InvalidOperationException(
                $"Prepared Palworld world path does not match the required dedicated world '{worldId}'.");
        }

        if (!File.Exists(Path.Combine(actualWorldPath, LevelSaveFileName)))
        {
            throw new InvalidOperationException(
                $"The prepared Palworld dedicated world has no {LevelSaveFileName}: {actualWorldPath}");
        }

        SelectDedicatedWorld(serverRoot, worldId);

        var process = Process.Start(new ProcessStartInfo
        {
            FileName = serverExecutable,
            WorkingDirectory = serverRoot,
            UseShellExecute = false
        }) ?? throw new InvalidOperationException("Palworld dedicated server failed to start.");

        return new GameSessionHandle(process.Id, DateTimeOffset.UtcNow);
    }

    private static EnvironmentManifest CreateDedicatedEnvironment(string worldId)
    {
        ValidateWorldId(worldId);
        return new EnvironmentManifest(
            SchemaVersion: 1,
            AdapterId: "palworld",
            GameVersion: "unknown",
            Components: [],
            Configuration: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [HostingModeKey] = DedicatedHostingMode,
                [DedicatedServerNameKey] = worldId
            });
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

    private static void SelectDedicatedWorld(string serverRoot, string worldId)
    {
        var configPath = Path.Combine(
            serverRoot,
            "Pal",
            "Saved",
            "Config",
            "WindowsServer",
            "GameUserSettings.ini");
        if (!File.Exists(configPath))
        {
            throw new InvalidOperationException(
                "PalServer has not initialized GameUserSettings.ini yet. Start the dedicated server once, stop it, then prepare the world again.");
        }

        var configText = File.ReadAllText(configPath);
        var match = DedicatedServerNameRegex().Match(configText);
        if (!match.Success)
        {
            throw new InvalidOperationException(
                $"DedicatedServerName was not found in PalServer configuration: {configPath}");
        }

        var updatedText = DedicatedServerNameRegex().Replace(
            configText,
            current => current.Groups["prefix"].Value + worldId,
            count: 1);
        if (string.Equals(configText, updatedText, StringComparison.Ordinal))
        {
            return;
        }

        var backupPath = configPath + ConfigBackupSuffix;
        if (!File.Exists(backupPath))
        {
            File.Copy(configPath, backupPath);
        }

        File.WriteAllText(configPath, updatedText);
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

    [GeneratedRegex(
        @"^(?<prefix>\s*DedicatedServerName\s*=\s*)[^\r\n]*",
        RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex DedicatedServerNameRegex();
}
