using SharedWorlds.Core.Abstractions;
using SharedWorlds.GameAdapters.Palworld;

var adapter = new PalworldAdapter();
var installations = await adapter.DiscoverInstallationsAsync();

Console.WriteLine("SharedWorlds Palworld probe (read-only)");
Console.WriteLine();

if (installations.Count == 0)
{
    Console.WriteLine("No Palworld Steam installation was detected.");
    return;
}

foreach (var installation in installations)
{
    Console.WriteLine($"Client installation: {installation.RootPath}");
    Console.WriteLine($"Source: {installation.Source}");

    if (installation.Metadata is not null)
    {
        foreach (var entry in installation.Metadata.OrderBy(entry => entry.Key, StringComparer.Ordinal))
        {
            Console.WriteLine($"  {entry.Key}: {entry.Value}");
        }
    }

    PrintDedicatedServerRuntimeState(installation.Metadata);

    var worlds = await adapter.DiscoverWorldsAsync(installation);
    Console.WriteLine($"Detected save Worlds: {worlds.Count}");

    foreach (var world in worlds)
    {
        Console.WriteLine($"  - {world.DisplayName}");
        Console.WriteLine($"    ID: {world.Id}");
        Console.WriteLine($"    Path: {world.SourcePath}");

        var levelPath = Path.Combine(world.SourcePath, "Level.sav");
        Console.WriteLine($"    Level.sav modified: {GetLastWriteTimeUtcSafe(levelPath):O}");
        Console.WriteLine($"    Level.sav size: {GetFileLengthSafe(levelPath):N0} bytes");

        var playersPath = Path.Combine(world.SourcePath, "Players");
        Console.WriteLine($"    Player save files: {CountFilesSafe(playersPath, "*.sav")}");

        var files = EnumerateFilesSafe(world.SourcePath)
            .Select(path => Path.GetRelativePath(world.SourcePath, path))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        Console.WriteLine($"    Total files in World directory: {files.Length}");
        Console.WriteLine($"    Total World directory size: {GetTotalSizeSafe(world.SourcePath):N0} bytes");

        var topLevelFiles = files
            .Where(path => !path.Contains(Path.DirectorySeparatorChar) &&
                           !path.Contains(Path.AltDirectorySeparatorChar))
            .ToArray();
        Console.WriteLine($"    Top-level files: {(topLevelFiles.Length == 0 ? "(none)" : string.Join(", ", topLevelFiles))}");
    }

    PrintWorldLayoutComparison(worlds);
    Console.WriteLine();
}

Console.WriteLine("Probe complete. No files were modified.");

static void PrintDedicatedServerRuntimeState(IReadOnlyDictionary<string, string>? metadata)
{
    if (metadata is null ||
        !metadata.TryGetValue("dedicatedServerRootPath", out var serverRoot) ||
        string.IsNullOrWhiteSpace(serverRoot))
    {
        return;
    }

    var savedRoot = Path.Combine(serverRoot, "Pal", "Saved");
    var configPath = Path.Combine(savedRoot, "Config", "WindowsServer");
    var saveGamesPath = Path.Combine(savedRoot, "SaveGames");
    var saveGamesRoot = Path.Combine(saveGamesPath, "0");

    Console.WriteLine("Dedicated server runtime state:");
    Console.WriteLine($"  initialized: {Directory.Exists(savedRoot)}");
    Console.WriteLine($"  savedRootPath: {savedRoot}");
    Console.WriteLine($"  windowsServerConfigPath: {configPath}");
    Console.WriteLine($"  saveGamesPath: {saveGamesPath}");
    Console.WriteLine($"  assumedSaveGamesRootPath: {saveGamesRoot}");

    var discoveredSaveDirectories = EnumerateDirectoriesSafe(saveGamesPath, SearchOption.AllDirectories)
        .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
        .ToArray();

    Console.WriteLine($"  discoveredDirectoriesUnderSaveGames: {discoveredSaveDirectories.Length}");
    foreach (var directory in discoveredSaveDirectories.Take(50))
    {
        Console.WriteLine($"    - {Path.GetRelativePath(saveGamesPath, directory)}");
    }

    if (discoveredSaveDirectories.Length > 50)
    {
        Console.WriteLine($"    ... {discoveredSaveDirectories.Length - 50} more directories omitted");
    }

    var serverWorldDirectories = EnumerateDirectoriesSafe(saveGamesRoot)
        .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
        .ToArray();

    Console.WriteLine($"  detectedServerWorldDirectories: {serverWorldDirectories.Length}");
    foreach (var worldDirectory in serverWorldDirectories)
    {
        var levelPath = Path.Combine(worldDirectory, "Level.sav");
        Console.WriteLine($"    - {Path.GetFileName(worldDirectory)}");
        Console.WriteLine($"      Path: {worldDirectory}");
        Console.WriteLine($"      Level.sav modified: {GetLastWriteTimeUtcSafe(levelPath):O}");
        Console.WriteLine($"      Level.sav size: {GetFileLengthSafe(levelPath):N0} bytes");
        Console.WriteLine($"      Total files: {EnumerateFilesSafe(worldDirectory).Count}");
        Console.WriteLine($"      Total size: {GetTotalSizeSafe(worldDirectory):N0} bytes");
    }
}

static void PrintWorldLayoutComparison(IReadOnlyList<DetectedWorld> worlds)
{
    var localWorld = worlds
        .Where(world => world.Id.StartsWith("local:", StringComparison.OrdinalIgnoreCase))
        .OrderByDescending(world => GetLastWriteTimeUtcSafe(Path.Combine(world.SourcePath, "Level.sav")))
        .FirstOrDefault();
    var dedicatedWorld = worlds
        .Where(world => world.Id.StartsWith("dedicated:", StringComparison.OrdinalIgnoreCase))
        .OrderByDescending(world => GetLastWriteTimeUtcSafe(Path.Combine(world.SourcePath, "Level.sav")))
        .FirstOrDefault();

    if (localWorld is null || dedicatedWorld is null)
    {
        return;
    }

    var comparer = OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;

    var localFiles = GetActiveRelativeFiles(localWorld.SourcePath)
        .ToDictionary(file => file.RelativePath, file => file.Size, comparer);
    var dedicatedFiles = GetActiveRelativeFiles(dedicatedWorld.SourcePath)
        .ToDictionary(file => file.RelativePath, file => file.Size, comparer);

    var onlyLocal = localFiles.Keys
        .Except(dedicatedFiles.Keys, comparer)
        .OrderBy(path => path, comparer)
        .ToArray();
    var onlyDedicated = dedicatedFiles.Keys
        .Except(localFiles.Keys, comparer)
        .OrderBy(path => path, comparer)
        .ToArray();
    var commonDifferentSize = localFiles.Keys
        .Intersect(dedicatedFiles.Keys, comparer)
        .Where(path => localFiles[path] != dedicatedFiles[path])
        .OrderBy(path => path, comparer)
        .ToArray();

    Console.WriteLine("World layout comparison (newest local vs newest dedicated, backup/* excluded):");
    Console.WriteLine($"  local: {localWorld.Id}");
    Console.WriteLine($"    Path: {localWorld.SourcePath}");
    Console.WriteLine($"    Active files: {localFiles.Count}");
    Console.WriteLine($"  dedicated: {dedicatedWorld.Id}");
    Console.WriteLine($"    Path: {dedicatedWorld.SourcePath}");
    Console.WriteLine($"    Active files: {dedicatedFiles.Count}");

    PrintPathList("onlyInLocal", onlyLocal);
    PrintPathList("onlyInDedicated", onlyDedicated);

    Console.WriteLine($"  commonFilesWithDifferentSize: {commonDifferentSize.Length}");
    foreach (var path in commonDifferentSize.Take(30))
    {
        Console.WriteLine($"    - {path}: local={localFiles[path]:N0} bytes, dedicated={dedicatedFiles[path]:N0} bytes");
    }

    if (commonDifferentSize.Length > 30)
    {
        Console.WriteLine($"    ... {commonDifferentSize.Length - 30} more files omitted");
    }
}

static IReadOnlyList<(string RelativePath, long Size)> GetActiveRelativeFiles(string worldPath)
{
    return EnumerateFilesSafe(worldPath)
        .Select(path => (RelativePath: Path.GetRelativePath(worldPath, path), Size: GetFileLengthSafe(path)))
        .Where(file => !IsUnderBackupDirectory(file.RelativePath))
        .OrderBy(file => file.RelativePath, StringComparer.OrdinalIgnoreCase)
        .ToArray();
}

static bool IsUnderBackupDirectory(string relativePath)
{
    var firstSeparator = relativePath.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]);
    var firstSegment = firstSeparator < 0 ? relativePath : relativePath[..firstSeparator];
    return string.Equals(firstSegment, "backup", StringComparison.OrdinalIgnoreCase);
}

static void PrintPathList(string label, IReadOnlyList<string> paths)
{
    Console.WriteLine($"  {label}: {paths.Count}");
    foreach (var path in paths.Take(30))
    {
        Console.WriteLine($"    - {path}");
    }

    if (paths.Count > 30)
    {
        Console.WriteLine($"    ... {paths.Count - 30} more files omitted");
    }
}

static DateTime GetLastWriteTimeUtcSafe(string path)
{
    try
    {
        return File.Exists(path) ? File.GetLastWriteTimeUtc(path) : DateTime.MinValue;
    }
    catch (IOException)
    {
        return DateTime.MinValue;
    }
    catch (UnauthorizedAccessException)
    {
        return DateTime.MinValue;
    }
}

static long GetFileLengthSafe(string path)
{
    try
    {
        return File.Exists(path) ? new FileInfo(path).Length : 0;
    }
    catch (IOException)
    {
        return 0;
    }
    catch (UnauthorizedAccessException)
    {
        return 0;
    }
}

static int CountFilesSafe(string path, string searchPattern)
{
    try
    {
        return Directory.Exists(path)
            ? Directory.EnumerateFiles(path, searchPattern, SearchOption.TopDirectoryOnly).Count()
            : 0;
    }
    catch (IOException)
    {
        return 0;
    }
    catch (UnauthorizedAccessException)
    {
        return 0;
    }
}

static IReadOnlyList<string> EnumerateDirectoriesSafe(
    string path,
    SearchOption searchOption = SearchOption.TopDirectoryOnly)
{
    try
    {
        return Directory.Exists(path)
            ? Directory.EnumerateDirectories(path, "*", searchOption).ToArray()
            : [];
    }
    catch (IOException)
    {
        return [];
    }
    catch (UnauthorizedAccessException)
    {
        return [];
    }
}

static IReadOnlyList<string> EnumerateFilesSafe(string path)
{
    try
    {
        return Directory.Exists(path)
            ? Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories).ToArray()
            : [];
    }
    catch (IOException)
    {
        return [];
    }
    catch (UnauthorizedAccessException)
    {
        return [];
    }
}

static long GetTotalSizeSafe(string path)
{
    long total = 0;
    foreach (var file in EnumerateFilesSafe(path))
    {
        total += GetFileLengthSafe(file);
    }

    return total;
}
