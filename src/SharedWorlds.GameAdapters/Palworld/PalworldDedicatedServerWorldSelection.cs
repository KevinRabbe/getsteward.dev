namespace SharedWorlds.GameAdapters.Palworld;

internal static partial class PalworldDedicatedServerHosting
{
    private const string ConfigBackupSuffix = ".sharedworlds-backup";

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
