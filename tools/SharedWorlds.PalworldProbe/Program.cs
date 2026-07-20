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
    var saveGamesRoot = Path.Combine(savedRoot, "SaveGames", "0");

    Console.WriteLine("Dedicated server runtime state:");
    Console.WriteLine($"  initialized: {Directory.Exists(savedRoot)}");
    Console.WriteLine($"  savedRootPath: {savedRoot}");
    Console.WriteLine($"  windowsServerConfigPath: {configPath}");
    Console.WriteLine($"  saveGamesRootPath: {saveGamesRoot}");

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

static IReadOnlyList<string> EnumerateDirectoriesSafe(string path)
{
    try
    {
        return Directory.Exists(path)
            ? Directory.EnumerateDirectories(path, "*", SearchOption.TopDirectoryOnly).ToArray()
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
