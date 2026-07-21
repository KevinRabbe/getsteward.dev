using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using SharedWorlds.Core.Abstractions;
using SharedWorlds.GameAdapters.Palworld;

var adapter = new PalworldAdapter();
var installations = await adapter.DiscoverInstallationsAsync();

Console.WriteLine("SharedWorlds Palworld canonical restore + host probe");
Console.WriteLine();

if (installations.Count == 0)
{
    Console.WriteLine("No Palworld Steam installation was detected.");
    Environment.ExitCode = 1;
    return;
}

if (IsProcessRunning("PalServer"))
{
    Console.WriteLine("PalServer is still running.");
    Console.WriteLine("Restore was refused because replacing an active world while the server may be writing is unsafe.");
    Console.WriteLine("Stop PalServer cleanly, then run this probe again.");
    Environment.ExitCode = 1;
    return;
}

var packagesRoot = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
    "SharedWorlds",
    "palworld",
    "packages");
var packagePath = Directory.Exists(packagesRoot)
    ? Directory
        .EnumerateFiles(packagesRoot, "*.zip", SearchOption.TopDirectoryOnly)
        .OrderByDescending(File.GetLastWriteTimeUtc)
        .FirstOrDefault()
    : null;

if (packagePath is null)
{
    Console.WriteLine($"No captured Palworld state package was found in: {packagesRoot}");
    Environment.ExitCode = 1;
    return;
}

var packageId = Path.GetFileNameWithoutExtension(packagePath);
Console.WriteLine($"Selected package: {packageId}");
Console.WriteLine($"Package path: {packagePath}");
Console.WriteLine();

var completed = false;
foreach (var installation in installations)
{
    var worlds = await adapter.DiscoverWorldsAsync(installation);
    var matchingWorld = worlds
        .Where(world => PackageMatchesWorld(packageId, world))
        .OrderByDescending(world => world.Id.StartsWith("dedicated:", StringComparison.OrdinalIgnoreCase))
        .ThenByDescending(world => GetLastWriteTimeUtcSafe(Path.Combine(world.SourcePath, "Level.sav")))
        .FirstOrDefault();

    if (matchingWorld is null)
    {
        continue;
    }

    Console.WriteLine($"Matched world: {matchingWorld.Id}");
    Console.WriteLine($"Matched path: {matchingWorld.SourcePath}");

    var environment = await adapter.InspectEnvironmentAsync(installation, matchingWorld);
    var prepared = await adapter.PrepareEnvironmentAsync(installation, environment);
    var state = new StatePackage(packageId, packagePath);

    Console.WriteLine($"Prepared dedicated path: {prepared.WorkingDirectory}");
    Console.WriteLine("Restoring canonical package...");

    await adapter.RestoreStateAsync(prepared, state);

    var verification = await VerifyRestoredPackageAsync(packagePath, prepared.WorkingDirectory);
    Console.WriteLine($"Restored files verified: {verification.FileCount}");
    Console.WriteLine($"All restored files match package bytes: {verification.AllFilesMatch}");
    Console.WriteLine($"Unexpected files after restore: {verification.UnexpectedFileCount}");
    Console.WriteLine($"Contains Level.sav: {verification.HasLevelSave}");

    if (!verification.AllFilesMatch ||
        verification.UnexpectedFileCount != 0 ||
        !verification.HasLevelSave)
    {
        throw new InvalidOperationException(
            "The restored Palworld world did not match the captured canonical package exactly.");
    }

    var session = await adapter.LaunchHostAsync(prepared);
    Console.WriteLine($"PalServer launched with PID: {session.ProcessId}");
    Console.WriteLine($"Started at: {session.StartedAt:O}");
    Console.WriteLine();
    Console.WriteLine("Canonical restore + host test complete.");
    Console.WriteLine("The host was prepared from the captured StatePackage rather than choosing between local and dedicated copies.");
    Console.WriteLine("Player identity migration remains outside this test.");
    completed = true;
    break;
}

if (!completed)
{
    Console.WriteLine("No detected Palworld world matched the newest captured package id.");
    Environment.ExitCode = 1;
}

static bool PackageMatchesWorld(string packageId, DetectedWorld world)
{
    var worldId = Path.GetFileName(Path.TrimEndingDirectorySeparator(world.SourcePath));
    return !string.IsNullOrWhiteSpace(worldId) &&
           packageId.StartsWith(worldId + "-", StringComparison.OrdinalIgnoreCase);
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

static async Task<(int FileCount, bool AllFilesMatch, int UnexpectedFileCount, bool HasLevelSave)>
    VerifyRestoredPackageAsync(string packagePath, string worldPath)
{
    var comparer = OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;
    var expectedFiles = new Dictionary<string, string>(comparer);

    using (var archive = ZipFile.OpenRead(packagePath))
    {
        foreach (var entry in archive.Entries.Where(entry => !string.IsNullOrEmpty(entry.Name)))
        {
            var relativePath = entry.FullName.Replace('/', Path.DirectorySeparatorChar);
            await using var stream = entry.Open();
            expectedFiles[relativePath] = await ComputeSha256Async(stream);
        }
    }

    var actualFiles = Directory
        .EnumerateFiles(worldPath, "*", SearchOption.AllDirectories)
        .Select(path => Path.GetRelativePath(worldPath, path))
        .ToArray();

    var allFilesMatch = true;
    foreach (var expected in expectedFiles)
    {
        var restoredPath = Path.Combine(worldPath, expected.Key);
        if (!File.Exists(restoredPath))
        {
            allFilesMatch = false;
            continue;
        }

        await using var stream = new FileStream(
            restoredPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 128 * 1024,
            useAsync: true);
        var actualHash = await ComputeSha256Async(stream);
        if (!string.Equals(expected.Value, actualHash, StringComparison.Ordinal))
        {
            allFilesMatch = false;
        }
    }

    var unexpectedFileCount = actualFiles.Count(path => !expectedFiles.ContainsKey(path));
    var hasLevelSave = expectedFiles.ContainsKey("Level.sav") &&
                       File.Exists(Path.Combine(worldPath, "Level.sav"));

    return (expectedFiles.Count, allFilesMatch, unexpectedFileCount, hasLevelSave);
}

static async Task<string> ComputeSha256Async(Stream stream)
{
    using var sha256 = SHA256.Create();
    return Convert.ToHexString(await sha256.ComputeHashAsync(stream));
}
