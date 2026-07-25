using System.Diagnostics;
using SharedWorlds.Core.Abstractions;

namespace SharedWorlds.GameAdapters.Palworld;

internal static partial class PalworldDedicatedServerHosting
{
    private const string ConfigBackupSuffix = ".sharedworlds-backup";

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
}
