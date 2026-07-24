namespace SharedWorlds.GameAdapters.Factorio;

internal static class FactorioModSettingsSafety
{
    private const string ModSettingsFileName = "mod-settings.dat";

    public static void RequireRegularFileIfPresent(string modsDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modsDirectory);

        var path = Path.GetFullPath(Path.Combine(modsDirectory, ModSettingsFileName));
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
                $"Steward could not inspect Factorio startup-settings file '{path}'.",
                exception);
        }

        if ((attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidOperationException(
                $"Factorio startup-settings file '{path}' is linked or a reparse point. Steward will not treat bytes outside the Factorio mod directory as this World's environment.");
        }
    }
}
