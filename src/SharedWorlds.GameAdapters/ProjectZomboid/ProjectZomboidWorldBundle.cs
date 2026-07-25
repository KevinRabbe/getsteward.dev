using SharedWorlds.Core.Abstractions;

namespace SharedWorlds.GameAdapters.ProjectZomboid;

internal static partial class ProjectZomboidWorldState
{
    private const string MapTimeFileName = "map_t.bin";
    private static readonly string[] ServerConfigSuffixes =
    [
        ".ini",
        "_SandboxVars.lua",
        "_spawnpoints.lua",
        "_spawnregions.lua"
    ];

    private static string ValidateSingleServerBundle(string userDataRoot)
    {
        var fullRoot = Path.GetFullPath(userDataRoot);
        var multiplayerRoot = Path.Combine(fullRoot, "Saves", "Multiplayer");
        if (!Directory.Exists(multiplayerRoot))
        {
            throw new InvalidOperationException(
                "Project Zomboid state package has no Saves/Multiplayer directory.");
        }

        var candidateWorlds = Directory
            .EnumerateDirectories(multiplayerRoot, "*", SearchOption.TopDirectoryOnly)
            .Where(path => File.Exists(Path.Combine(path, MapTimeFileName)))
            .ToArray();
        if (candidateWorlds.Length != 1)
        {
            throw new InvalidOperationException(
                $"Project Zomboid state package must contain exactly one authoritative multiplayer World; found {candidateWorlds.Length}.");
        }

        var serverName = GetServerName(candidateWorlds[0]);
        ValidateServerBundle(fullRoot, serverName, requireExclusiveBundle: true);
        return serverName;
    }

    private static void ValidateServerBundle(
        string userDataRoot,
        string serverName,
        bool requireExclusiveBundle)
    {
        var worldRoot = Path.Combine(userDataRoot, "Saves", "Multiplayer", serverName);
        if (!Directory.Exists(worldRoot) ||
            !File.Exists(Path.Combine(worldRoot, MapTimeFileName)))
        {
            throw new InvalidOperationException(
                $"Project Zomboid server '{serverName}' has no authoritative multiplayer World with {MapTimeFileName}.");
        }

        var serverRoot = Path.Combine(userDataRoot, "Server");
        var mainConfigPath = Path.Combine(serverRoot, serverName + ".ini");
        if (!File.Exists(mainConfigPath))
        {
            throw new InvalidOperationException(
                $"Project Zomboid server '{serverName}' has no matching Server/{serverName}.ini definition.");
        }

        if (!requireExclusiveBundle)
        {
            return;
        }

        var allowedTopLevelDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Saves",
            "Server",
            "db"
        };
        foreach (var directory in Directory.EnumerateDirectories(userDataRoot, "*", SearchOption.TopDirectoryOnly))
        {
            if (!allowedTopLevelDirectories.Contains(Path.GetFileName(directory)))
            {
                throw new InvalidOperationException(
                    $"Project Zomboid state package contains unexpected top-level directory '{Path.GetFileName(directory)}'.");
            }
        }

        if (Directory.EnumerateFiles(userDataRoot, "*", SearchOption.TopDirectoryOnly).Any())
        {
            throw new InvalidOperationException(
                "Project Zomboid state package contains unexpected files at the workspace root.");
        }

        var savesRoot = Path.Combine(userDataRoot, "Saves");
        var savesDirectories = Directory.Exists(savesRoot)
            ? Directory.EnumerateDirectories(savesRoot, "*", SearchOption.TopDirectoryOnly).ToArray()
            : [];
        if (savesDirectories.Length != 1 ||
            !string.Equals(Path.GetFileName(savesDirectories[0]), "Multiplayer", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "Project Zomboid state package may contain only Saves/Multiplayer state.");
        }

        var multiplayerDirectories = Directory
            .EnumerateDirectories(Path.Combine(savesRoot, "Multiplayer"), "*", SearchOption.TopDirectoryOnly)
            .ToArray();
        if (multiplayerDirectories.Length != 1 ||
            !string.Equals(Path.GetFileName(multiplayerDirectories[0]), serverName, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "Project Zomboid state package contains state for more than one multiplayer server.");
        }

        if (Directory.Exists(serverRoot))
        {
            var allowedServerFiles = ServerConfigSuffixes
                .Select(suffix => serverName + suffix)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var filePath in Directory.EnumerateFiles(serverRoot, "*", SearchOption.AllDirectories))
            {
                if (!PathsEqual(Path.GetDirectoryName(filePath)!, serverRoot) ||
                    !allowedServerFiles.Contains(Path.GetFileName(filePath)))
                {
                    throw new InvalidOperationException(
                        $"Project Zomboid state package contains unexpected server configuration '{Path.GetRelativePath(userDataRoot, filePath)}'.");
                }
            }
        }

        var dbRoot = Path.Combine(userDataRoot, "db");
        if (Directory.Exists(dbRoot))
        {
            var expectedDatabaseName = serverName + ".db";
            foreach (var filePath in Directory.EnumerateFiles(dbRoot, "*", SearchOption.AllDirectories))
            {
                if (!PathsEqual(Path.GetDirectoryName(filePath)!, dbRoot) ||
                    !string.Equals(Path.GetFileName(filePath), expectedDatabaseName, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(
                        $"Project Zomboid state package contains unexpected database file '{Path.GetRelativePath(userDataRoot, filePath)}'.");
                }
            }
        }
    }

    private static string GetRequiredUserDataRoot(GameInstallation installation)
    {
        if (installation.Metadata is null ||
            !installation.Metadata.TryGetValue(ProjectZomboidInstallationDiscovery.UserDataPathKey, out var userDataPath) ||
            string.IsNullOrWhiteSpace(userDataPath))
        {
            throw new InvalidOperationException(
                "Project Zomboid installation is missing its user-data path.");
        }

        return Path.GetFullPath(userDataPath);
    }

    private static string GetServerName(string worldPath)
    {
        var serverName = Path.GetFileName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(worldPath)));
        if (string.IsNullOrWhiteSpace(serverName) ||
            serverName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            throw new InvalidOperationException(
                $"Could not determine a safe Project Zomboid server name from '{worldPath}'.");
        }

        return serverName;
    }

    private static bool PathsEqual(string left, string right)
        => string.Equals(
            Path.GetFullPath(left),
            Path.GetFullPath(right),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
}
