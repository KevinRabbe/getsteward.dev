using SharedWorlds.Core.Abstractions;

namespace SharedWorlds.GameAdapters.Necesse;

internal static class NecesseWorkshopGuard
{
    internal static void RequireNoAmbiguousWorkshopContent(GameInstallation installation)
    {
        ArgumentNullException.ThrowIfNull(installation);
        if (installation.Metadata is null ||
            !installation.Metadata.TryGetValue(
                NecesseInstallationDiscovery.SteamManifestPathKey,
                out var manifestPath) ||
            string.IsNullOrWhiteSpace(manifestPath))
        {
            throw new InvalidOperationException(
                "Necesse's Steam manifest path is unavailable, so Steward cannot inspect Steam Workshop content for the vanilla environment proof.");
        }

        var fullManifestPath = Path.GetFullPath(manifestPath);
        var steamAppsRoot = Path.GetDirectoryName(fullManifestPath);
        if (string.IsNullOrWhiteSpace(steamAppsRoot))
        {
            throw new InvalidOperationException(
                $"Necesse's Steam manifest has no usable steamapps parent directory: {fullManifestPath}");
        }

        RequireNoAmbiguousWorkshopContentAt(
            Path.Combine(
                steamAppsRoot,
                "workshop",
                "content",
                NecesseInstallationDiscovery.GameSteamAppId));
    }

    internal static void RequireNoAmbiguousWorkshopContentAt(string workshopContentRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workshopContentRoot);
        var fullRoot = Path.GetFullPath(workshopContentRoot);

        FileAttributes attributes;
        try
        {
            attributes = File.GetAttributes(fullRoot);
        }
        catch (Exception exception) when (
            exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException(
                $"Steward could not safely inspect Necesse's Steam Workshop content root: {fullRoot}",
                exception);
        }

        if ((attributes & FileAttributes.Directory) == 0 ||
            (attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidOperationException(
                $"Necesse's Steam Workshop content root is linked or is not a regular directory: {fullRoot}");
        }

        try
        {
            if (Directory.EnumerateFileSystemEntries(fullRoot).Any())
            {
                throw new InvalidOperationException(
                    "Necesse has installed Steam Workshop payload. Steward's current vanilla-only adapter cannot prove this installation is vanilla-exact before relying on Necesse's materialized local mod state; installed Workshop content is not being claimed as active.");
            }
        }
        catch (InvalidOperationException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException(
                $"Steward could not safely inspect Necesse's Steam Workshop content root: {fullRoot}",
                exception);
        }
    }
}
