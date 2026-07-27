namespace SharedWorlds.GameAdapters.Astroneer;

internal static class AstroneerInstalledModGuard
{
    internal const string StockClientPakName = "pakchunk0-WindowsNoEditor.pak";

    private static readonly string[] Ue4SsRootMarkers =
    [
        "ue4ss",
        "UE4SS.dll",
        "UE4SS-settings.ini",
        "dwmapi.dll",
        "xinput1_3.dll"
    ];

    internal static void RequireNoInstallRootMods(string installationRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(installationRoot);
        var root = Path.GetFullPath(installationRoot);

        RequireNoUe4SsMarkers(Path.Combine(root, "Astro", "Binaries", "Win64"));
        RequireStockClientPaksOnly(Path.Combine(root, "Astro", "Content", "Paks"));
    }

    internal static void RequireNoUe4SsMarkers(string win64Root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(win64Root);
        var fullRoot = Path.GetFullPath(win64Root);
        if (!TryRequireRegularDirectory(fullRoot, "ASTRONEER's Win64 install directory"))
        {
            return;
        }

        foreach (var markerName in Ue4SsRootMarkers)
        {
            var markerPath = Path.Combine(fullRoot, markerName);
            if (!PathExists(markerPath))
            {
                continue;
            }

            RequireRegularPath(markerPath, $"ASTRONEER UE4SS marker '{markerName}'");
            throw new InvalidOperationException(
                $"ASTRONEER's install root contains UE4SS marker '{markerName}'. Steward's current ASTRONEER adapter is vanilla-only.");
        }
    }

    internal static void RequireStockClientPaksOnly(string paksRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(paksRoot);
        var fullRoot = Path.GetFullPath(paksRoot);
        if (!TryRequireRegularDirectory(fullRoot, "ASTRONEER's install-root Paks directory"))
        {
            return;
        }

        try
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(
                         fullRoot,
                         "*",
                         SearchOption.TopDirectoryOnly))
            {
                var name = Path.GetFileName(entry);
                var attributes = File.GetAttributes(entry);
                if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0 ||
                    !string.Equals(name, StockClientPakName, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(
                        $"ASTRONEER's install-root Paks directory contains non-stock or linked content '{name}'. Steward's current ASTRONEER adapter is vanilla-only.");
                }
            }
        }
        catch (InvalidOperationException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException(
                $"Steward could not safely inspect ASTRONEER's install-root Paks directory: {fullRoot}",
                exception);
        }
    }

    private static bool TryRequireRegularDirectory(string path, string description)
    {
        try
        {
            var attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.Directory) == 0 ||
                (attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidOperationException(
                    $"{description} is linked or is not a regular directory: {path}");
            }

            return true;
        }
        catch (Exception exception) when (
            exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException(
                $"Steward could not safely inspect {description}: {path}",
                exception);
        }
    }

    private static bool PathExists(string path)
    {
        try
        {
            _ = File.GetAttributes(path);
            return true;
        }
        catch (Exception exception) when (
            exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return false;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException(
                $"Steward could not safely inspect ASTRONEER install-root path: {path}",
                exception);
        }
    }

    private static void RequireRegularPath(string path, string description)
    {
        try
        {
            var attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidOperationException(
                    $"{description} is linked. Steward's current ASTRONEER adapter is vanilla-only: {path}");
            }
        }
        catch (InvalidOperationException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException(
                $"Steward could not safely inspect {description}: {path}",
                exception);
        }
    }
}
