using System.Diagnostics;
using System.IO.Compression;
using SharedWorlds.Core.Abstractions;
using SharedWorlds.GameAdapters.Palworld;

var hostRequested = args.Any(arg =>
    string.Equals(arg, "--host", StringComparison.OrdinalIgnoreCase));
var captureRequested = args.Any(arg =>
    string.Equals(arg, "--capture", StringComparison.OrdinalIgnoreCase));
var restAcceptanceRequested = args.Any(arg =>
    string.Equals(arg, "--rest-acceptance", StringComparison.OrdinalIgnoreCase));

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
var restAcceptanceBlocked = false;

Console.WriteLine(hostRequested
    ? "SharedWorlds Palworld probe (host test)"
    : captureRequested
        ? "SharedWorlds Palworld probe (state capture test)"
        : restAcceptanceRequested
            ? "SharedWorlds Palworld probe (REST acceptance configuration)"
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

    if (restAcceptanceRequested)
    {
        string? serverRoot = null;
        var hasServerRoot = installation.Metadata is not null &&
            installation.Metadata.TryGetValue(
                PalworldInstallationDiscovery.DedicatedServerRootPathKey,
                out serverRoot);

        Console.WriteLine("REST acceptance configuration:");
        if (!hasServerRoot || string.IsNullOrWhiteSpace(serverRoot))
        {
            Console.WriteLine("  productionLifecycleReady: false");
            Console.WriteLine("  blockingReasons:");
            Console.WriteLine("    - dedicated server root was not discovered");
            restAcceptanceBlocked = true;
        }
        else
        {
            var configuration = PalworldRestAcceptanceConfigurationReader.Read(serverRoot);
            Console.WriteLine($"  configPath: {configuration.ConfigPath}");
            Console.WriteLine($"  configExists: {configuration.ConfigExists}");
            Console.WriteLine($"  restEnabled: {configuration.RestEnabled}");
            Console.WriteLine($"  restPort: {(configuration.RestPort?.ToString() ?? "(none)")}");
            Console.WriteLine($"  adminPasswordConfigured: {configuration.AdminPasswordConfigured}");
            Console.WriteLine($"  selectedWorldId: {configuration.SelectedWorldId ?? "(none)"}");
            Console.WriteLine($"  configurationReady: {configuration.IsUsable}");
            if (configuration.BlockingReasons.Count > 0)
            {
                Console.WriteLine("  blockingReasons:");
                foreach (var reason in configuration.BlockingReasons)
                {
                    Console.WriteLine($"    - {reason}");
                }

                restAcceptanceBlocked = true;
            }
            else if (!await RunRestAcceptanceAsync(installation, adapter, serverRoot, configuration))
            {
                restAcceptanceBlocked = true;
            }
        }

        Console.WriteLine();
        continue;
    }

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

if (restAcceptanceRequested && restAcceptanceBlocked)
{
    Environment.ExitCode = 2;
}

static async Task<bool> RunRestAcceptanceAsync(
    GameInstallation installation,
    PalworldAdapter adapter,
    string serverRoot,
    PalworldRestAcceptanceConfiguration configuration)
{
    Console.WriteLine();
    Console.WriteLine("Palworld REST live acceptance");

    var serverExecutable = installation.Metadata is not null &&
        installation.Metadata.TryGetValue(
            PalworldInstallationDiscovery.DedicatedServerExecutablePathKey,
            out var discoveredExecutable)
        ? discoveredExecutable
        : null;
    if (string.IsNullOrWhiteSpace(serverExecutable) || !File.Exists(serverExecutable))
    {
        Console.WriteLine("  blockingReasons: PalServer.exe was not discovered.");
        return false;
    }

    var worlds = await adapter.DiscoverWorldsAsync(installation);
    var selectedWorld = !string.IsNullOrWhiteSpace(configuration.SelectedWorldId)
        ? worlds.FirstOrDefault(world =>
            world.Id.EndsWith(":" + configuration.SelectedWorldId, StringComparison.OrdinalIgnoreCase) &&
            world.Id.StartsWith("dedicated:", StringComparison.OrdinalIgnoreCase))
        : worlds
            .Where(world => world.Id.StartsWith("dedicated:", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(world => GetLastWriteTimeUtcSafe(Path.Combine(world.SourcePath, "Level.sav")))
            .FirstOrDefault();
    if (selectedWorld is null ||
        !Directory.Exists(selectedWorld.SourcePath) ||
        !File.Exists(Path.Combine(selectedWorld.SourcePath, "Level.sav")))
    {
        Console.WriteLine("  blockingReasons: no valid dedicated World was available for acceptance.");
        return false;
    }

    if (IsProcessRunning("PalServer"))
    {
        Console.WriteLine("  blockingReasons: PalServer is already running; stop it before acceptance.");
        return false;
    }

    var password = PalworldRestAcceptanceConfigurationReader.ReadAdminPassword(serverRoot);
    if (string.IsNullOrWhiteSpace(password))
    {
        Console.WriteLine("  blockingReasons: AdminPassword could not be read for transient acceptance use.");
        return false;
    }

    var beforeSave = SnapshotWorldFiles(selectedWorld.SourcePath);
    Process? process = null;
    var forcedCleanupUsed = false;
    var productionLifecycleReady = false;
    try
    {
        var processStartedAt = DateTimeOffset.UtcNow;
        process = Process.Start(new ProcessStartInfo
        {
            FileName = serverExecutable,
            WorkingDirectory = serverRoot,
            UseShellExecute = false,
            CreateNoWindow = true
        });
        if (process is null)
        {
            Console.WriteLine("  blockingReasons: PalServer could not be started.");
            return false;
        }

        Console.WriteLine("session:");
        Console.WriteLine($"  processStarted: {processStartedAt:O}");
        Console.WriteLine($"  pid: {process.Id}");
        Console.WriteLine("  worldId: " + selectedWorld.Id);

        var restPort = configuration.RestPort ?? throw new InvalidOperationException("REST port was not available after configuration validation.");
        using var httpClient = new HttpClient
        {
            BaseAddress = new Uri($"http://127.0.0.1:{restPort}/", UriKind.Absolute),
            Timeout = TimeSpan.FromSeconds(5)
        };
        var restClient = new PalworldRestApiClient(httpClient, "admin", password);
        PalworldServerInfo? serverInfo = null;
        var transportFailures = 0;
        var authenticationFailures = 0;
        var malformedResponseFailures = 0;
        var timeoutFailures = 0;
        var lastReadinessFailure = "none";
        var readinessStartedAt = DateTimeOffset.UtcNow;
        DateTimeOffset? readyAt = null;
        var readinessDeadline = readinessStartedAt + TimeSpan.FromSeconds(90);
        while (DateTimeOffset.UtcNow < readinessDeadline)
        {
            if (process.HasExited)
            {
                break;
            }

            try
            {
                serverInfo = await restClient.GetInfoAsync();
                readyAt = DateTimeOffset.UtcNow;
                break;
            }
            catch (HttpRequestException)
            {
                transportFailures++;
                lastReadinessFailure = "transport";
                await Task.Delay(TimeSpan.FromSeconds(1));
            }
            catch (InvalidOperationException)
            {
                authenticationFailures++;
                lastReadinessFailure = "authentication";
                await Task.Delay(TimeSpan.FromSeconds(1));
            }
            catch (InvalidDataException)
            {
                malformedResponseFailures++;
                lastReadinessFailure = "malformed-response";
                await Task.Delay(TimeSpan.FromSeconds(1));
            }
            catch (TaskCanceledException)
            {
                timeoutFailures++;
                lastReadinessFailure = "timeout";
                await Task.Delay(TimeSpan.FromSeconds(1));
            }
        }

        Console.WriteLine("readiness:");
        Console.WriteLine($"  success: {serverInfo is not null}");
        Console.WriteLine($"  elapsed: {(readyAt is null ? "(none)" : (readyAt.Value - readinessStartedAt).ToString())}");
        if (serverInfo is null)
        {
            Console.WriteLine($"  lastFailure: {lastReadinessFailure}");
            Console.WriteLine($"  transportFailures: {transportFailures}");
            Console.WriteLine($"  authenticationFailures: {authenticationFailures}");
            Console.WriteLine($"  malformedResponseFailures: {malformedResponseFailures}");
            Console.WriteLine($"  timeoutFailures: {timeoutFailures}");
            PrintListenerObservation(restPort);
            Console.WriteLine("  blockingReasons: authenticated REST /info did not become ready before the bounded timeout.");
            return false;
        }

        Console.WriteLine($"  version: {serverInfo.Version}");
        Console.WriteLine($"  serverName: {serverInfo.ServerName}");
        Console.WriteLine($"  worldGuid: {serverInfo.WorldGuid}");
        PrintListenerObservation(restPort);

        var saveRequestedAt = DateTimeOffset.UtcNow;
        var saveRequestSucceeded = false;
        try
        {
            await restClient.SaveAsync();
            saveRequestSucceeded = true;
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or InvalidOperationException)
        {
            Console.WriteLine($"  saveError: {exception.Message}");
        }

        var saveObservation = await ObserveWorldStabilityAsync(
            selectedWorld.SourcePath,
            beforeSave,
            TimeSpan.FromSeconds(30),
            TimeSpan.FromSeconds(3));
        Console.WriteLine("save:");
        Console.WriteLine($"  requestSucceeded: {saveRequestSucceeded}");
        Console.WriteLine($"  requestTimestamp: {saveRequestedAt:O}");
        Console.WriteLine($"  filesystemWriteObserved: {saveObservation.ChangedFiles.Count > 0}");
        Console.WriteLine($"  changedFiles: {(saveObservation.ChangedFiles.Count == 0 ? "(none)" : string.Join(", ", saveObservation.ChangedFiles))}");
        Console.WriteLine($"  stabilizationTime: {FormatDuration(saveObservation.StabilizedAt - saveRequestedAt)}");

        var beforeShutdown = SnapshotWorldFiles(selectedWorld.SourcePath);
        var shutdownRequestedAt = DateTimeOffset.UtcNow;
        var shutdownRequestSucceeded = false;
        try
        {
            await restClient.ShutdownAsync(1, "Steward acceptance probe shutdown.");
            shutdownRequestSucceeded = true;
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or InvalidOperationException)
        {
            Console.WriteLine($"  shutdownError: {exception.Message}");
        }

        var processExited = await WaitForExitAsync(process, TimeSpan.FromSeconds(60));
        var shutdownObservation = await ObserveWorldStabilityAsync(
            selectedWorld.SourcePath,
            beforeShutdown,
            TimeSpan.FromSeconds(30),
            TimeSpan.FromSeconds(3));
        var exitAt = processExited ? DateTimeOffset.UtcNow : (DateTimeOffset?)null;
        Console.WriteLine("shutdown:");
        Console.WriteLine($"  requestSucceeded: {shutdownRequestSucceeded}");
        Console.WriteLine($"  requestTimestamp: {shutdownRequestedAt:O}");
        Console.WriteLine($"  processExited: {processExited}");
        Console.WriteLine($"  exitLatency: {(exitAt is null ? "(none)" : FormatDuration(exitAt.Value - shutdownRequestedAt))}");
        Console.WriteLine($"  writesAfterShutdownRequest: {shutdownObservation.ChangedFiles.Count > 0}");
        Console.WriteLine($"  finalStabilizationTime: {FormatDuration(shutdownObservation.StabilizedAt - shutdownRequestedAt)}");

        productionLifecycleReady = saveRequestSucceeded &&
            saveObservation.Stabilized &&
            shutdownRequestSucceeded &&
            processExited &&
            shutdownObservation.Stabilized;
        Console.WriteLine($"productionLifecycleReady: {productionLifecycleReady}");
        if (!productionLifecycleReady)
        {
            Console.WriteLine("blockingReasons: one or more save, stabilization, shutdown, or process-exit conditions were not proven.");
        }

        return productionLifecycleReady;
    }
    finally
    {
        password = string.Empty;
        if (process is not null)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    forcedCleanupUsed = true;
                    Console.WriteLine("forcedCleanupUsed: true");
                }
            }
            catch (InvalidOperationException)
            {
                // The process exited between observation and cleanup.
            }
            finally
            {
                process.Dispose();
            }
        }

        if (forcedCleanupUsed)
        {
            Console.WriteLine("productionLifecycleReady: false");
        }
    }
}

static void PrintListenerObservation(int port)
{
    var endpoints = System.Net.NetworkInformation.IPGlobalProperties.GetIPGlobalProperties()
        .GetActiveTcpListeners()
        .Where(endpoint => endpoint.Port == port)
        .Select(endpoint => endpoint.ToString())
        .OrderBy(endpoint => endpoint, StringComparer.Ordinal)
        .ToArray();
    var exposure = endpoints.Length == 0
        ? "not-observed"
        : endpoints.All(endpoint => endpoint.StartsWith("127.0.0.1:", StringComparison.Ordinal) || endpoint.StartsWith("[::1]:", StringComparison.Ordinal))
            ? "loopback-only"
            : endpoints.Any(endpoint => endpoint.StartsWith("0.0.0.0:", StringComparison.Ordinal) || endpoint.StartsWith("[::]:", StringComparison.Ordinal))
                ? "wildcard-or-all-interfaces"
                : "specific-interface";
    Console.WriteLine("listener:");
    Console.WriteLine($"  binding: {(endpoints.Length == 0 ? "(none)" : string.Join(", ", endpoints))}");
    Console.WriteLine($"  exposure: {exposure}");
}

static async Task<bool> WaitForExitAsync(Process process, TimeSpan timeout)
{
    var deadline = DateTimeOffset.UtcNow + timeout;
    while (DateTimeOffset.UtcNow < deadline)
    {
        if (process.HasExited)
        {
            return true;
        }

        await Task.Delay(TimeSpan.FromMilliseconds(250));
    }

    return process.HasExited;
}

static async Task<WorldObservation> ObserveWorldStabilityAsync(
    string worldPath,
    IReadOnlyDictionary<string, WorldFileState> baseline,
    TimeSpan maximumDuration,
    TimeSpan stabilityWindow)
{
    var changedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    var previous = baseline;
    DateTimeOffset? firstChange = null;
    DateTimeOffset? lastChange = null;
    DateTimeOffset? stableSince = null;
    var deadline = DateTimeOffset.UtcNow + maximumDuration;
    while (DateTimeOffset.UtcNow < deadline)
    {
        var current = SnapshotWorldFiles(worldPath);
        var changed = GetChangedFiles(previous, current);
        foreach (var changedFile in GetChangedFiles(baseline, current))
        {
            changedFiles.Add(changedFile);
        }

        var now = DateTimeOffset.UtcNow;
        if (changed.Count > 0)
        {
            firstChange ??= now;
            lastChange = now;
            stableSince = null;
        }
        else
        {
            stableSince ??= now;
            if (now - stableSince >= stabilityWindow)
            {
                return new(changedFiles.OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToArray(), firstChange, lastChange, now, true);
            }
        }

        previous = current;
        await Task.Delay(TimeSpan.FromMilliseconds(500));
    }

    return new(changedFiles.OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToArray(), firstChange, lastChange, DateTimeOffset.UtcNow, false);
}

static IReadOnlyList<string> GetChangedFiles(
    IReadOnlyDictionary<string, WorldFileState> before,
    IReadOnlyDictionary<string, WorldFileState> after)
{
    return before.Keys.Union(after.Keys, StringComparer.OrdinalIgnoreCase)
        .Where(path => !before.TryGetValue(path, out var oldState) ||
                       !after.TryGetValue(path, out var newState) ||
                       oldState != newState)
        .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
        .ToArray();
}

static IReadOnlyDictionary<string, WorldFileState> SnapshotWorldFiles(string worldPath)
{
    var snapshot = new Dictionary<string, WorldFileState>(StringComparer.OrdinalIgnoreCase);
    foreach (var filePath in Directory.EnumerateFiles(worldPath, "*", SearchOption.AllDirectories))
    {
        var relativePath = Path.GetRelativePath(worldPath, filePath);
        if (IsExcludedWorldPath(relativePath))
        {
            continue;
        }

        var info = new FileInfo(filePath);
        snapshot[relativePath] = new(info.Length, info.LastWriteTimeUtc);
    }

    return snapshot;
}

static bool IsExcludedWorldPath(string relativePath)
{
    return relativePath
        .Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries)
        .Any(segment => string.Equals(segment, "backup", StringComparison.OrdinalIgnoreCase) ||
                        segment.Contains(".sharedworlds-backup", StringComparison.OrdinalIgnoreCase) ||
                        segment.Contains(".sharedworlds-staging-", StringComparison.OrdinalIgnoreCase));
}

static string FormatDuration(TimeSpan duration) => duration < TimeSpan.Zero ? "(before observation)" : duration.ToString();

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
