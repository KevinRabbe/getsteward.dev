using System.Diagnostics;
using System.IO.Compression;
using SharedWorlds.Core.Abstractions;
using SharedWorlds.GameAdapters.Palworld;

var hostRequested = args.Any(arg =>
    string.Equals(arg, "--host", StringComparison.OrdinalIgnoreCase));
var captureRequested = args.Any(arg =>
    string.Equals(arg, "--capture", StringComparison.OrdinalIgnoreCase));

if (hostRequested && captureRequested)
{
    Console.Error.WriteLine("Use either --host or --capture, not both in the same probe run.");
    Environment.ExitCode = 2;
    return;
}

var adapter = new PalworldAdapter();
var installations = await adapter.DiscoverInstallationsAsync();
var hostStarted = false;
var captureCompleted = false;
var captureBlockedByRunningServer = false;

Console.WriteLine(hostRequested
    ? "SharedWorlds Palworld probe (host test)"
    : captureRequested
        ? "SharedWorlds Palworld probe (state capture test)"
        : "SharedWorlds Palworld probe (read-only)");
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

    if (hostRequested && !hostStarted)
    {
        var localWorld = worlds
            .Where(world => world.Id.StartsWith("local:", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(world => GetLastWriteTimeUtcSafe(Path.Combine(world.SourcePath, "Level.sav")))
            .FirstOrDefault();

        Console.WriteLine();
        Console.WriteLine("Host test:");

        if (localWorld is null)
        {
            Console.WriteLine("  No local Palworld world was detected for hosting.");
        }
        else
        {
            Console.WriteLine($"  Selected local world: {localWorld.Id}");
            Console.WriteLine($"  Source path: {localWorld.SourcePath}");

            var prepared = await adapter.PrepareDetectedWorldForHostingAsync(installation, localWorld);
            Console.WriteLine($"  Prepared dedicated world: {prepared.WorkingDirectory}");

            var session = await adapter.LaunchHostAsync(prepared);
            Console.WriteLine($"  PalServer launched with PID: {session.ProcessId}");
            Console.WriteLine($"  Started at: {session.StartedAt:O}");
            Console.WriteLine("  Player identity migration is not applied by this host test.");
            hostStarted = true;
        }
    }

    if (captureRequested && !captureCompleted && !captureBlockedByRunningServer)
    {
        var dedicatedWorld = worlds
            .Where(world => world.Id.StartsWith("dedicated:", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(world => GetLastWriteTimeUtcSafe(Path.Combine(world.SourcePath, "Level.sav")))
            .FirstOrDefault();

        Console.WriteLine();
        Console.WriteLine("State capture test:");

        if (dedicatedWorld is null)
        {
            Console.WriteLine("  No dedicated Palworld world was detected for capture.");
        }
        else if (IsProcessRunning("PalServer"))
        {
            Console.WriteLine("  PalServer is still running.");
            Console.WriteLine("  Capture was refused so the canonical package is not created from a world that may still be changing.");
            Console.WriteLine("  Stop PalServer cleanly, then run the probe with --capture again.");
            captureBlockedByRunningServer = true;
        }
        else
        {
            Console.WriteLine($"  Selected dedicated world: {dedicatedWorld.Id}");
            Console.WriteLine($"  Source path: {dedicatedWorld.SourcePath}");

            var captured = await adapter.CaptureDetectedWorldAsync(installation, dedicatedWorld);
            var packageInspection = InspectStatePackage(captured.Package.Path);

            Console.WriteLine($"  Package id: {captured.Package.Id}");
            Console.WriteLine($"  Package path: {captured.Package.Path}");
            Console.WriteLine($"  Captured at: {captured.CapturedAt:O}");
            Console.WriteLine($"  Package entries: {packageInspection.EntryCount}");
            Console.WriteLine($"  Contains Level.sav: {packageInspection.HasLevelSave}");
            Console.WriteLine($"  Contains excluded backup data: {packageInspection.HasBackupData}");
            Console.WriteLine($"  Delete after durable store: {captured.DeletePackageAfterStore}");

            if (!packageInspection.HasLevelSave || packageInspection.HasBackupData)
            {
                throw new InvalidOperationException(
                    "The captured Palworld package failed structural validation.");
            }

            captureCompleted = true;
        }
    }

    Console.WriteLine();
}

if (hostRequested)
{
    if (hostStarted)
    {
        Console.WriteLine("Host test complete. PalServer was launched through the Palworld adapter.");
    }
    else
    {
        Console.WriteLine("Host test failed to find a local world that could be launched.");
        Environment.ExitCode = 1;
    }
}
else if (captureRequested)
{
    if (captureCompleted)
    {
        Console.WriteLine("State capture test complete. A portable Palworld package was created through the adapter.");
        Console.WriteLine("The probe leaves the temporary package in place because no durable State store is wired into this test yet.");
    }
    else
    {
        Console.WriteLine("State capture test did not create a package.");
        Environment.ExitCode = 1;
    }
}
else
{
    Console.WriteLine("Probe complete. No files were modified.");
}

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
        .Where(path => !IsSharedWorldsInternalDirectoryName(Path.GetFileName(path)))
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
    return relativePath
        .Split(
            new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
            StringSplitOptions.RemoveEmptyEntries)
        .Any(segment => string.Equals(segment, "backup", StringComparison.OrdinalIgnoreCase));
}

static bool IsSharedWorldsInternalDirectoryName(string directoryName)
{
    return directoryName.Contains(".sharedworlds-backup", StringComparison.OrdinalIgnoreCase) ||
           directoryName.Contains(".sharedworlds-staging-", StringComparison.OrdinalIgnoreCase);
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

static bool IsProcessRunning(string processName)
{
    foreach (var process in Process.GetProcessesByName(processName))
    {
        using (process)
        {
            try
            {
                if (!process.HasExited)
                {
                    return true;
                }
            }
            catch (InvalidOperationException)
            {
                // Process disappeared during inspection.
            }
        }
    }

    return false;
}

static (int EntryCount, bool HasLevelSave, bool HasBackupData) InspectStatePackage(string packagePath)
{
    using var archive = ZipFile.OpenRead(packagePath);
    var hasLevelSave = archive.Entries.Any(entry =>
        string.Equals(entry.FullName, "Level.sav", StringComparison.OrdinalIgnoreCase));
    var hasBackupData = archive.Entries.Any(entry =>
        entry.FullName
            .Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Any(segment => string.Equals(segment, "backup", StringComparison.OrdinalIgnoreCase)));

    return (archive.Entries.Count, hasLevelSave, hasBackupData);
}
