using SharedWorlds.Core.Abstractions;

namespace SharedWorlds.GameAdapters.SevenDaysToDie;

internal static partial class SevenDaysToDieWorldState
{
    private static WorldIdentity ValidateSingleWorldBundle(string userDataRoot)
    {
        var fullRoot = Path.GetFullPath(userDataRoot);
        var savesRoot = Path.Combine(fullRoot, "Saves");
        if (!Directory.Exists(savesRoot))
        {
            throw new InvalidOperationException(
                "7 Days to Die state package has no Saves directory.");
        }

        var candidates = new List<WorldIdentity>();
        foreach (var worldDirectory in Directory.EnumerateDirectories(savesRoot, "*", SearchOption.TopDirectoryOnly))
        {
            var worldName = Path.GetFileName(Path.TrimEndingDirectorySeparator(worldDirectory));
            if (string.IsNullOrWhiteSpace(worldName))
            {
                continue;
            }

            foreach (var saveDirectory in Directory.EnumerateDirectories(worldDirectory, "*", SearchOption.TopDirectoryOnly))
            {
                if (!SevenDaysToDieWorldDiscovery.HasKnownWorldMarker(saveDirectory))
                {
                    continue;
                }

                var gameName = Path.GetFileName(Path.TrimEndingDirectorySeparator(saveDirectory));
                if (!string.IsNullOrWhiteSpace(gameName))
                {
                    candidates.Add(new WorldIdentity(worldName, gameName));
                }
            }
        }

        if (candidates.Count != 1)
        {
            throw new InvalidOperationException(
                $"7 Days to Die state package must contain exactly one World save; found {candidates.Count}.");
        }

        var identity = candidates[0];
        ValidateWorldBundle(
            fullRoot,
            identity.WorldName,
            identity.GameName,
            requireExclusiveBundle: true);
        return identity;
    }

    private static void ValidateWorldBundle(
        string userDataRoot,
        string worldName,
        string gameName,
        bool requireExclusiveBundle)
    {
        var saveRoot = Path.Combine(userDataRoot, "Saves", worldName, gameName);
        if (!Directory.Exists(saveRoot) ||
            !SevenDaysToDieWorldDiscovery.HasKnownWorldMarker(saveRoot))
        {
            throw new InvalidOperationException(
                $"7 Days to Die save '{gameName}' in World '{worldName}' has no recognized root World marker.");
        }

        if (!requireExclusiveBundle)
        {
            return;
        }

        var allowedTopLevelDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Saves",
            "GeneratedWorlds"
        };
        foreach (var directory in Directory.EnumerateDirectories(userDataRoot, "*", SearchOption.TopDirectoryOnly))
        {
            if (!allowedTopLevelDirectories.Contains(Path.GetFileName(directory)))
            {
                throw new InvalidOperationException(
                    $"7 Days to Die state package contains unexpected top-level directory '{Path.GetFileName(directory)}'.");
            }
        }

        if (Directory.EnumerateFiles(userDataRoot, "*", SearchOption.TopDirectoryOnly).Any())
        {
            throw new InvalidOperationException(
                "7 Days to Die state package contains unexpected files at the workspace root.");
        }

        var savesRoot = Path.Combine(userDataRoot, "Saves");
        var worldDirectories = Directory.EnumerateDirectories(savesRoot, "*", SearchOption.TopDirectoryOnly).ToArray();
        if (worldDirectories.Length != 1 ||
            !string.Equals(Path.GetFileName(worldDirectories[0]), worldName, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "7 Days to Die state package contains saves for more than one GameWorld.");
        }

        var saveDirectories = Directory.EnumerateDirectories(worldDirectories[0], "*", SearchOption.TopDirectoryOnly).ToArray();
        if (saveDirectories.Length != 1 ||
            !string.Equals(Path.GetFileName(saveDirectories[0]), gameName, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "7 Days to Die state package contains more than one GameName save.");
        }

        var generatedWorldsRoot = Path.Combine(userDataRoot, "GeneratedWorlds");
        if (Directory.Exists(generatedWorldsRoot))
        {
            if (Directory.EnumerateFiles(generatedWorldsRoot, "*", SearchOption.TopDirectoryOnly).Any())
            {
                throw new InvalidOperationException(
                    "7 Days to Die state package contains unexpected files directly under GeneratedWorlds.");
            }

            var generatedWorldDirectories = Directory
                .EnumerateDirectories(generatedWorldsRoot, "*", SearchOption.TopDirectoryOnly)
                .ToArray();
            if (generatedWorldDirectories.Length != 1 ||
                !string.Equals(
                    Path.GetFileName(generatedWorldDirectories[0]),
                    worldName,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "7 Days to Die state package contains generated terrain for a different or additional GameWorld.");
            }
        }
    }

    private static WorldIdentity GetDetectedWorldIdentity(
        string userDataRoot,
        string sourcePath)
    {
        var fullRoot = Path.GetFullPath(userDataRoot);
        var fullSource = Path.GetFullPath(sourcePath);
        var relativePath = Path.GetRelativePath(fullRoot, fullSource);
        var segments = relativePath.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length != 3 ||
            !string.Equals(segments[0], "Saves", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(segments[1]) ||
            string.IsNullOrWhiteSpace(segments[2]))
        {
            throw new InvalidOperationException(
                "The detected 7 Days to Die World is not beneath the installation's Saves/<GameWorld>/<GameName> root.");
        }

        return new WorldIdentity(segments[1], segments[2]);
    }

    private static string GetRequiredUserDataRoot(GameInstallation installation)
    {
        if (installation.Metadata is null ||
            !installation.Metadata.TryGetValue(SevenDaysToDieInstallationDiscovery.UserDataPathKey, out var userDataPath) ||
            string.IsNullOrWhiteSpace(userDataPath))
        {
            throw new InvalidOperationException(
                "7 Days to Die installation is missing its user-data path.");
        }

        return Path.GetFullPath(userDataPath);
    }

    private sealed record WorldIdentity(string WorldName, string GameName);
}
