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

    public static PreparedWorld PrepareDetectedWorld(
        GameInstallation installation,
        DetectedWorld world)
    {
        ArgumentNullException.ThrowIfNull(installation);
        ArgumentNullException.ThrowIfNull(world);

        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException(
                "The Palworld dedicated-host preparation path is currently validated only on Windows.");
        }

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

        var sourceWorldPath = Path.GetFullPath(world.SourcePath);
        if (!Directory.Exists(sourceWorldPath) ||
            !File.Exists(Path.Combine(sourceWorldPath, LevelSaveFileName)))
        {
            throw new InvalidOperationException(
                $"The detected Palworld world is no longer available: {sourceWorldPath}");
        }

        var worldId = Path.GetFileName(Path.TrimEndingDirectorySeparator(sourceWorldPath));
        if (string.IsNullOrWhiteSpace(worldId))
        {
            throw new InvalidOperationException(
                $"Could not determine the Palworld world id from: {sourceWorldPath}");
        }

        var saveGamesRoot = Path.Combine(
            serverRoot,
            "Pal",
            "Saved",
            "SaveGames",
            DedicatedProfileId);
        Directory.CreateDirectory(saveGamesRoot);

        var destinationWorldPath = Path.GetFullPath(Path.Combine(saveGamesRoot, worldId));
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

        SelectDedicatedWorld(serverRoot, worldId);

        var environment = new EnvironmentManifest(
            SchemaVersion: 1,
            AdapterId: "palworld",
            GameVersion: "unknown",
            Components: [],
            Configuration: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["hostingMode"] = "dedicated-server",
                ["dedicatedServerName"] = worldId
            });

        return new PreparedWorld(
            Installation: installation,
            WorkingDirectory: destinationWorldPath,
            Environment: environment,
            DisplayName: world.DisplayName);
    }

    public static GameSessionHandle Launch(PreparedWorld world)
    {
        ArgumentNullException.ThrowIfNull(world);

        var serverRoot = GetRequiredMetadata(
            world.Installation,
            PalworldInstallationDiscovery.DedicatedServerRootPathKey);
        var serverExecutable = GetRequiredMetadata(
            world.Installation,
            PalworldInstallationDiscovery.DedicatedServerExecutablePathKey);

        if (!File.Exists(serverExecutable))
        {
            throw new InvalidOperationException(
                $"The Palworld dedicated server executable is no longer available: {serverExecutable}");
        }

        var process = Process.Start(new ProcessStartInfo
        {
            FileName = serverExecutable,
            WorkingDirectory = serverRoot,
            UseShellExecute = false
        }) ?? throw new InvalidOperationException("Palworld dedicated server failed to start.");

        return new GameSessionHandle(process.Id, DateTimeOffset.UtcNow);
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
