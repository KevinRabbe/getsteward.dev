using System.Text.Json;
using SharedWorlds.Core.Abstractions;

namespace SharedWorlds.GameAdapters.Enshrouded;

internal static class EnshroudedWorldDiscovery
{
    internal const long MaximumIndexBytes = 64L * 1024;

    internal static readonly IReadOnlyList<(string Id, string DisplayName)> KnownWorlds =
    [
        ("3ad85aea", "World 1"),
        ("3bd85c7d", "World 2"),
        ("38d857c4", "World 3"),
        ("39d85957", "World 4"),
        ("36d8549e", "World 5"),
        ("37d85631", "World 6"),
        ("34d85178", "World 7"),
        ("35d8530b", "World 8"),
        ("32d84e52", "World 9"),
        ("33d84fe5", "World 10")
    ];

    public static IReadOnlyList<DetectedWorld> Discover(GameInstallation installation)
    {
        ArgumentNullException.ThrowIfNull(installation);
        if (installation.Metadata is null ||
            !installation.Metadata.TryGetValue(
                EnshroudedInstallationDiscovery.SaveGamesRootPathKey,
                out var saveGamesRoot) ||
            string.IsNullOrWhiteSpace(saveGamesRoot))
        {
            return [];
        }

        return DiscoverFromSaveGamesRoot(saveGamesRoot);
    }

    internal static IReadOnlyList<DetectedWorld> DiscoverFromSaveGamesRoot(string saveGamesRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(saveGamesRoot);
        var fullRoot = Path.GetFullPath(saveGamesRoot);
        if (!IsRegularDirectory(fullRoot))
        {
            return [];
        }

        var worlds = new List<DetectedWorld>();
        foreach (var (worldId, displayName) in KnownWorlds)
        {
            if (!TryResolveCurrentBundle(fullRoot, worldId, out var bundle))
            {
                continue;
            }

            worlds.Add(new DetectedWorld(
                Id: $"local:{worldId}",
                DisplayName: displayName,
                SourcePath: bundle.DataIndexPath));
        }

        return worlds
            .OrderByDescending(world => GetCurrentDataWriteTimeUtcSafe(world.SourcePath))
            .ThenBy(world => world.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    internal static EnshroudedNativeBundle ResolveFromDataIndexPath(string dataIndexPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataIndexPath);
        var fullIndexPath = Path.GetFullPath(dataIndexPath);
        var fileName = Path.GetFileName(fullIndexPath);
        if (!fileName.EndsWith("-index", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Enshrouded World source must be a data index file: {fullIndexPath}");
        }

        var worldId = fileName[..^"-index".Length];
        if (!IsKnownWorldId(worldId))
        {
            throw new InvalidOperationException(
                $"Enshrouded World index has an unsupported World identity: {fullIndexPath}");
        }

        var saveRoot = Path.GetDirectoryName(fullIndexPath)
            ?? throw new InvalidOperationException(
                $"Could not determine Enshrouded save root for '{fullIndexPath}'.");
        if (!TryResolveCurrentBundle(saveRoot, worldId, out var bundle) ||
            !PathsEqual(bundle.DataIndexPath, fullIndexPath))
        {
            throw new InvalidOperationException(
                $"Enshrouded World index does not resolve to a complete current World bundle: {fullIndexPath}");
        }

        return bundle;
    }

    internal static bool TryResolveCurrentBundle(
        string saveGamesRoot,
        string worldId,
        out EnshroudedNativeBundle bundle)
    {
        bundle = default!;
        if (!IsKnownWorldId(worldId))
        {
            return false;
        }

        var fullRoot = Path.GetFullPath(saveGamesRoot);
        if (!IsRegularDirectory(fullRoot))
        {
            return false;
        }

        var dataIndexPath = Path.Combine(fullRoot, $"{worldId}-index");
        var infoIndexPath = Path.Combine(fullRoot, $"{worldId}_info-index");
        if (!TryReadIndex(dataIndexPath, out var dataIndex) ||
            !TryReadIndex(infoIndexPath, out var infoIndex) ||
            dataIndex.Deleted ||
            infoIndex.Deleted)
        {
            return false;
        }

        var dataPath = Path.Combine(fullRoot, GetDataFileName(worldId, dataIndex.Latest));
        var infoPath = Path.Combine(fullRoot, GetInfoFileName(worldId, infoIndex.Latest));
        if (!IsRegularNonEmptyFile(dataPath) || !IsRegularNonEmptyFile(infoPath))
        {
            return false;
        }

        bundle = new EnshroudedNativeBundle(
            worldId,
            Path.GetFullPath(dataIndexPath),
            Path.GetFullPath(dataPath),
            dataIndex.Latest,
            Path.GetFullPath(infoIndexPath),
            Path.GetFullPath(infoPath),
            infoIndex.Latest);
        return true;
    }

    internal static string GetDataFileName(string worldId, int latest)
        => latest == 0 ? worldId : $"{worldId}-{latest}";

    internal static string GetInfoFileName(string worldId, int latest)
        => latest == 0 ? $"{worldId}_info" : $"{worldId}_info-{latest}";

    internal static bool IsKnownWorldId(string worldId)
        => KnownWorlds.Any(world => string.Equals(world.Id, worldId, StringComparison.Ordinal));

    internal static bool IsRegularNonEmptyFile(string path)
    {
        try
        {
            var attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.Directory) != 0 ||
                (attributes & FileAttributes.ReparsePoint) != 0)
            {
                return false;
            }

            return new FileInfo(path).Length > 0;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    internal static bool IsRegularDirectory(string path)
    {
        try
        {
            var attributes = File.GetAttributes(path);
            return (attributes & FileAttributes.Directory) != 0 &&
                   (attributes & FileAttributes.ReparsePoint) == 0;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool TryReadIndex(string path, out EnshroudedIndexRecord index)
    {
        index = default;
        try
        {
            if (!IsRegularNonEmptyFile(path))
            {
                return false;
            }

            var info = new FileInfo(path);
            if (info.Length > MaximumIndexBytes)
            {
                return false;
            }

            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 16 * 1024,
                useAsync: false);
            using var document = JsonDocument.Parse(stream);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("latest", out var latestElement) ||
                !latestElement.TryGetInt32(out var latest) ||
                latest is < 0 or > 9)
            {
                return false;
            }

            var deleted = false;
            if (root.TryGetProperty("deleted", out var deletedElement))
            {
                if (deletedElement.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                {
                    return false;
                }

                deleted = deletedElement.GetBoolean();
            }

            index = new EnshroudedIndexRecord(latest, deleted);
            return true;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return false;
        }
    }

    private static DateTime GetCurrentDataWriteTimeUtcSafe(string dataIndexPath)
    {
        try
        {
            var bundle = ResolveFromDataIndexPath(dataIndexPath);
            return File.GetLastWriteTimeUtc(bundle.DataPath);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return DateTime.MinValue;
        }
    }

    private static bool PathsEqual(string left, string right)
        => string.Equals(
            Path.GetFullPath(left),
            Path.GetFullPath(right),
            OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal);
}

internal readonly record struct EnshroudedIndexRecord(int Latest, bool Deleted);

internal sealed record EnshroudedNativeBundle(
    string WorldId,
    string DataIndexPath,
    string DataPath,
    int DataLatest,
    string InfoIndexPath,
    string InfoPath,
    int InfoLatest);
