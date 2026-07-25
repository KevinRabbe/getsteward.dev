using SharedWorlds.Core.Abstractions;

namespace SharedWorlds.GameAdapters.Factorio;

internal static class FactorioModInputSafety
{
    private const string ModListFileName = "mod-list.json";
    private const string ModSettingsFileName = "mod-settings.dat";

    public static void RequireInspectionInputs(GameInstallation installation)
    {
        var modsDirectory = GetModsDirectory(installation);
        RequireRegularModsDirectoryIfPresent(modsDirectory);
        RequireRegularFileIfPresent(modsDirectory, ModListFileName, "mod-list");
        RequireRegularFileIfPresent(modsDirectory, ModSettingsFileName, "startup-settings");
    }

    public static void RequireReproductionInputs(GameInstallation installation)
    {
        var modsDirectory = GetModsDirectory(installation);
        RequireRegularModsDirectoryIfPresent(modsDirectory);
        RequireRegularFileIfPresent(modsDirectory, ModSettingsFileName, "startup-settings");
    }

    internal static void RequireRegularFileIfPresent(
        string modsDirectory,
        string fileName,
        string description)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modsDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        ArgumentException.ThrowIfNullOrWhiteSpace(description);

        var path = Path.GetFullPath(Path.Combine(modsDirectory, fileName));
        if (!File.Exists(path))
        {
            return;
        }

        FileAttributes attributes;
        try
        {
            attributes = File.GetAttributes(path);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException(
                $"Steward could not inspect Factorio {description} file '{path}'.",
                exception);
        }

        if ((attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidOperationException(
                $"Factorio {description} file '{path}' is linked or a reparse point. Steward will not treat bytes outside the Factorio mod directory as this World's environment.");
        }
    }

    private static void RequireRegularModsDirectoryIfPresent(string modsDirectory)
    {
        var path = Path.GetFullPath(modsDirectory);
        FileAttributes attributes;
        try
        {
            attributes = File.GetAttributes(path);
        }
        catch (Exception exception) when (
            exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException(
                $"Steward could not inspect Factorio mods directory '{path}'.",
                exception);
        }

        if ((attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidOperationException(
                $"Factorio mods directory '{path}' is linked or a reparse point. Steward will not treat a redirected directory as this World's environment.");
        }

        if ((attributes & FileAttributes.Directory) == 0)
        {
            throw new InvalidOperationException(
                $"Factorio mods path '{path}' is not a directory.");
        }
    }

    private static string GetModsDirectory(GameInstallation installation)
    {
        ArgumentNullException.ThrowIfNull(installation);
        if (installation.Metadata is null ||
            !installation.Metadata.TryGetValue(
                FactorioInstallationDiscovery.UserDataPathKey,
                out var userDataPath) ||
            string.IsNullOrWhiteSpace(userDataPath))
        {
            throw new InvalidOperationException(
                $"Factorio installation '{installation.RootPath}' is missing required user-data metadata.");
        }

        return Path.Combine(userDataPath, "mods");
    }
}
