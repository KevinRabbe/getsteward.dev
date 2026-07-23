using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SharedWorlds.GameAdapters.Palworld;

const string LauncherProcessName = "PalServer";
const string ShippingProcessName = "PalServer-Win64-Shipping-Cmd";

Console.WriteLine("SharedWorlds Palworld WorldOption startup-precedence acceptance");
Console.WriteLine();

if (!OperatingSystem.IsWindows())
{
    Console.Error.WriteLine("This acceptance probe requires the real Windows Palworld dedicated server.");
    Environment.ExitCode = 2;
    return;
}

var succeeded = await RunAsync();
Environment.ExitCode = succeeded ? 0 : 2;

static async Task<bool> RunAsync()
{
    var adapter = new PalworldAdapter();
    var installations = await adapter.DiscoverInstallationsAsync();
    var candidates = installations
        .Where(installation =>
            installation.Metadata is not null &&
            installation.Metadata.TryGetValue(PalworldInstallationDiscovery.DedicatedServerRootPathKey, out var root) &&
            !string.IsNullOrWhiteSpace(root) &&
            installation.Metadata.TryGetValue(PalworldInstallationDiscovery.DedicatedServerExecutablePathKey, out var executable) &&
            !string.IsNullOrWhiteSpace(executable))
        .ToArray();

    if (candidates.Length != 1)
    {
        Console.Error.WriteLine(
            $"Expected exactly one discovered Palworld installation with a dedicated server, found {candidates.Length}.");
        return false;
    }

    var installation = candidates[0];
    var metadata = installation.Metadata!;
    var serverRoot = metadata[PalworldInstallationDiscovery.DedicatedServerRootPathKey];
    var serverExecutable = metadata[PalworldInstallationDiscovery.DedicatedServerExecutablePathKey];
    if (!File.Exists(serverExecutable))
    {
        Console.Error.WriteLine("PalServer.exe was not found at the discovered dedicated-server path.");
        return false;
    }

    var configuration = PalworldRestAcceptanceConfigurationReader.Read(serverRoot);
    Console.WriteLine("configuration:");
    Console.WriteLine($"  configPath: {configuration.ConfigPath}");
    Console.WriteLine($"  restEnabled: {configuration.RestEnabled}");
    Console.WriteLine($"  restPort: {(configuration.RestPort?.ToString() ?? "(none)")}");
    Console.WriteLine($"  iniAdminPasswordConfigured: {configuration.AdminPasswordConfigured}");
    Console.WriteLine($"  selectedWorldId: {configuration.SelectedWorldId ?? "(none)"}");
    if (!configuration.ConfigExists ||
        !configuration.RestEnabled ||
        configuration.RestPort is null ||
        !configuration.AdminPasswordConfigured ||
        string.IsNullOrWhiteSpace(configuration.SelectedWorldId))
    {
        Console.Error.WriteLine("  blockingReason: the existing PalWorldSettings.ini and selected World are not ready for this probe.");
        return false;
    }

    var password = PalworldRestAcceptanceConfigurationReader.ReadAdminPassword(serverRoot);
    if (string.IsNullOrWhiteSpace(password))
    {
        Console.Error.WriteLine("The existing INI AdminPassword could not be read for transient acceptance use.");
        return false;
    }

    var worlds = await adapter.DiscoverWorldsAsync(installation);
    var selectedWorld = worlds.SingleOrDefault(world =>
        world.Id.StartsWith("dedicated:", StringComparison.OrdinalIgnoreCase) &&
        string.Equals(
            Path.GetFileName(Path.TrimEndingDirectorySeparator(world.SourcePath)),
            configuration.SelectedWorldId,
            StringComparison.OrdinalIgnoreCase));
    if (selectedWorld is null || !File.Exists(Path.Combine(selectedWorld.SourcePath, "Level.sav")))
    {
        password = string.Empty;
        Console.Error.WriteLine("The selected dedicated World was not discovered with a valid Level.sav.");
        return false;
    }

    if (IsAnyPalworldProcessRunning())
    {
        password = string.Empty;
        Console.Error.WriteLine("PalServer is already running. Stop it before the startup-precedence probe.");
        return false;
    }

    var worldOptionPath = Path.Combine(selectedWorld.SourcePath, "WorldOption.sav");
    if (!File.Exists(worldOptionPath))
    {
        password = string.Empty;
        Console.Error.WriteLine("The active WorldOption.sav is missing; this probe requires the canonical file to be present.");
        return false;
    }

    var originalBytes = await File.ReadAllBytesAsync(worldOptionPath);
    var originalHash = Sha256(originalBytes);
    var parkingPath = worldOptionPath + $".sharedworlds-startup-probe-{Guid.NewGuid():N}";

    Console.WriteLine();
    Console.WriteLine("worldOption:");
    Console.WriteLine($"  worldId: {configuration.SelectedWorldId}");
    Console.WriteLine($"  path: {worldOptionPath}");
    Console.WriteLine($"  originalSha256: {originalHash}");
    Console.WriteLine("  experiment: park canonical file -> launch from INI -> restore a byte-exact live copy -> observe");

    Process? launcher = null;
    var parked = false;
    var liveCopyRestored = false;
    var generatedBeforeRestore = false;
    var readyBeforeRestore = false;
    var worldGuidMatched = false;
    var settingsBeforeRestoreReadable = false;
    var authSurvivedRestore = false;
    var settingsUnchangedAfterRestore = false;
    var shutdownSucceeded = false;
    var allProcessesExited = false;
    var forcedCleanupUsed = false;
    var liveCopyMutatedByPalworld = false;
    var canonicalOriginalRestored = false;
    var finalOriginalHashMatches = false;

    try
    {
        File.Move(worldOptionPath, parkingPath);
        parked = true;
        if (File.Exists(worldOptionPath))
        {
            throw new IOException("WorldOption.sav still exists after it was parked.");
        }

        launcher = Process.Start(new ProcessStartInfo
        {
            FileName = serverExecutable,
            WorkingDirectory = serverRoot,
            UseShellExecute = false,
            CreateNoWindow = true
        });
        if (launcher is null)
        {
            Console.Error.WriteLine("PalServer.exe could not be started.");
            return false;
        }

        Console.WriteLine();
        Console.WriteLine("session:");
        Console.WriteLine($"  launcherPid: {launcher.Id}");

        using var httpClient = new HttpClient
        {
            BaseAddress = new Uri($"http://127.0.0.1:{configuration.RestPort.Value}/", UriKind.Absolute),
            Timeout = TimeSpan.FromSeconds(5)
        };
        var restClient = new PalworldRestApiClient(httpClient, "admin", password);

        var readiness = await WaitForInfoAsync(restClient, launcher, TimeSpan.FromSeconds(90));
        readyBeforeRestore = readiness.Info is not null;
        Console.WriteLine("beforeRestore:");
        Console.WriteLine($"  restReady: {readyBeforeRestore}");
        Console.WriteLine($"  elapsed: {readiness.Elapsed}");
        Console.WriteLine($"  lastFailure: {readiness.LastFailure}");
        if (!readyBeforeRestore)
        {
            Console.Error.WriteLine("  blockingReason: authenticated REST /info did not become ready while WorldOption.sav was parked.");
            return false;
        }

        worldGuidMatched = string.Equals(
            readiness.Info!.WorldGuid,
            configuration.SelectedWorldId,
            StringComparison.OrdinalIgnoreCase);
        Console.WriteLine($"  worldGuid: {readiness.Info.WorldGuid}");
        Console.WriteLine($"  worldGuidMatched: {worldGuidMatched}");
        if (!worldGuidMatched)
        {
            Console.Error.WriteLine("  blockingReason: REST reported a different active World.");
            return false;
        }

        using var settingsBefore = await restClient.GetSettingsAsync();
        var baselineSettings = SnapshotSettings(settingsBefore.RootElement);
        settingsBeforeRestoreReadable = baselineSettings.Count > 0;
        Console.WriteLine($"  settingsReadable: {settingsBeforeRestoreReadable}");
        Console.WriteLine($"  settingsPropertyCount: {baselineSettings.Count}");
        Console.WriteLine($"  settingsFingerprint: {SettingsFingerprint(baselineSettings)}");
        if (!settingsBeforeRestoreReadable)
        {
            Console.Error.WriteLine("  blockingReason: REST /settings did not return a usable object.");
            return false;
        }

        if (File.Exists(worldOptionPath))
        {
            generatedBeforeRestore = true;
            Console.Error.WriteLine(
                "PalServer generated WorldOption.sav while the canonical file was parked; refusing to overwrite it while the server is running.");
            return false;
        }

        await File.WriteAllBytesAsync(worldOptionPath, originalBytes);
        liveCopyRestored = true;
        var liveCopyHash = await Sha256FileAsync(worldOptionPath);
        if (!string.Equals(liveCopyHash, originalHash, StringComparison.Ordinal))
        {
            Console.Error.WriteLine("The live WorldOption copy did not restore byte-for-byte.");
            return false;
        }

        Console.WriteLine();
        Console.WriteLine("afterLiveRestore:");
        Console.WriteLine("  liveCopySha256MatchesOriginal: true");

        var checkpoints = new[]
        {
            (Name: "immediate", Delay: TimeSpan.Zero),
            (Name: "+3s", Delay: TimeSpan.FromSeconds(3)),
            (Name: "+10s", Delay: TimeSpan.FromSeconds(7))
        };

        authSurvivedRestore = true;
        settingsUnchangedAfterRestore = true;
        foreach (var checkpoint in checkpoints)
        {
            if (checkpoint.Delay > TimeSpan.Zero)
            {
                await Task.Delay(checkpoint.Delay);
            }

            var infoSucceeded = false;
            var settingsSucceeded = false;
            var equivalent = false;
            IReadOnlyList<string> changedNames = Array.Empty<string>();
            try
            {
                var info = await restClient.GetInfoAsync();
                infoSucceeded = string.Equals(
                    info.WorldGuid,
                    configuration.SelectedWorldId,
                    StringComparison.OrdinalIgnoreCase);
                using var settings = await restClient.GetSettingsAsync();
                var snapshot = SnapshotSettings(settings.RootElement);
                settingsSucceeded = snapshot.Count > 0;
                changedNames = GetChangedSettingNames(baselineSettings, snapshot);
                equivalent = changedNames.Count == 0;
            }
            catch (Exception exception) when (
                exception is HttpRequestException or
                InvalidOperationException or
                InvalidDataException or
                TaskCanceledException)
            {
                Console.WriteLine($"  {checkpoint.Name}.failure: {ClassifyRestFailure(exception)}");
            }

            Console.WriteLine($"  {checkpoint.Name}.authenticatedInfo: {infoSucceeded}");
            Console.WriteLine($"  {checkpoint.Name}.settingsReadable: {settingsSucceeded}");
            Console.WriteLine($"  {checkpoint.Name}.settingsUnchanged: {equivalent}");
            if (changedNames.Count > 0)
            {
                Console.WriteLine($"  {checkpoint.Name}.changedSettingNames: {string.Join(", ", changedNames)}");
            }

            authSurvivedRestore &= infoSucceeded;
            settingsUnchangedAfterRestore &= settingsSucceeded && equivalent;
        }

        await restClient.ShutdownAsync(1, "Steward startup-precedence acceptance shutdown.");
        shutdownSucceeded = true;
        allProcessesExited = await WaitForPalworldExitAsync(launcher, TimeSpan.FromSeconds(60));
        Console.WriteLine();
        Console.WriteLine("shutdown:");
        Console.WriteLine($"  requestSucceeded: {shutdownSucceeded}");
        Console.WriteLine($"  allPalworldProcessesExited: {allProcessesExited}");
        if (!allProcessesExited)
        {
            Console.Error.WriteLine("Palworld did not fully exit within the graceful-shutdown window.");
        }

        if (File.Exists(worldOptionPath))
        {
            var afterExitHash = await Sha256FileAsync(worldOptionPath);
            liveCopyMutatedByPalworld = !string.Equals(afterExitHash, originalHash, StringComparison.Ordinal);
            Console.WriteLine($"  liveWorldOptionMutatedByPalworld: {liveCopyMutatedByPalworld}");
        }

        return readyBeforeRestore &&
            worldGuidMatched &&
            settingsBeforeRestoreReadable &&
            liveCopyRestored &&
            authSurvivedRestore &&
            settingsUnchangedAfterRestore &&
            shutdownSucceeded &&
            allProcessesExited;
    }
    catch (Exception exception) when (
        exception is IOException or
        UnauthorizedAccessException or
        HttpRequestException or
        InvalidOperationException or
        InvalidDataException or
        TaskCanceledException)
    {
        Console.Error.WriteLine($"Startup-precedence acceptance failed: {exception.Message}");
        return false;
    }
    finally
    {
        password = string.Empty;

        if (launcher is not null)
        {
            try
            {
                if (!launcher.HasExited || IsAnyPalworldProcessRunning())
                {
                    forcedCleanupUsed = await ForceCleanupPalworldProcessesAsync();
                }
            }
            catch (InvalidOperationException)
            {
                // The launcher exited while cleanup state was being observed.
            }
            finally
            {
                launcher.Dispose();
            }
        }

        try
        {
            // The parked file is the untouched canonical original. Whatever PalServer may
            // have done to a live copy is discarded only after every Palworld process is gone.
            if (parked && File.Exists(parkingPath))
            {
                if (File.Exists(worldOptionPath))
                {
                    File.Delete(worldOptionPath);
                }

                File.Move(parkingPath, worldOptionPath);
                parked = false;
                canonicalOriginalRestored = true;
            }
            else if (File.Exists(worldOptionPath))
            {
                var currentHash = await Sha256FileAsync(worldOptionPath);
                canonicalOriginalRestored = string.Equals(currentHash, originalHash, StringComparison.Ordinal);
                if (!canonicalOriginalRestored)
                {
                    await WriteAtomicallyAsync(worldOptionPath, originalBytes);
                    canonicalOriginalRestored = true;
                }
            }

            if (File.Exists(worldOptionPath))
            {
                finalOriginalHashMatches = string.Equals(
                    await Sha256FileAsync(worldOptionPath),
                    originalHash,
                    StringComparison.Ordinal);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"CRITICAL: canonical WorldOption restoration failed: {exception.Message}");
        }

        Console.WriteLine();
        Console.WriteLine("result:");
        Console.WriteLine($"  readyWithWorldOptionParked: {readyBeforeRestore}");
        Console.WriteLine($"  worldGuidMatched: {worldGuidMatched}");
        Console.WriteLine($"  settingsBeforeRestoreReadable: {settingsBeforeRestoreReadable}");
        Console.WriteLine($"  generatedWorldOptionBeforeRestore: {generatedBeforeRestore}");
        Console.WriteLine($"  liveCopyRestored: {liveCopyRestored}");
        Console.WriteLine($"  authSurvivedLiveRestore: {authSurvivedRestore}");
        Console.WriteLine($"  settingsUnchangedAfterLiveRestore: {settingsUnchangedAfterRestore}");
        Console.WriteLine($"  shutdownSucceeded: {shutdownSucceeded}");
        Console.WriteLine($"  allPalworldProcessesExited: {allProcessesExited}");
        Console.WriteLine($"  liveCopyMutatedByPalworld: {liveCopyMutatedByPalworld}");
        Console.WriteLine($"  forcedCleanupUsed: {forcedCleanupUsed}");
        Console.WriteLine($"  canonicalOriginalRestored: {canonicalOriginalRestored}");
        Console.WriteLine($"  finalOriginalSha256Matches: {finalOriginalHashMatches}");
        Console.WriteLine($"startupOnlyWorldOptionPrecedenceProven: {readyBeforeRestore && worldGuidMatched && settingsBeforeRestoreReadable && liveCopyRestored && authSurvivedRestore && settingsUnchangedAfterRestore && shutdownSucceeded && allProcessesExited && !forcedCleanupUsed && canonicalOriginalRestored && finalOriginalHashMatches}");
    }
}

static async Task<ReadinessResult> WaitForInfoAsync(
    PalworldRestApiClient restClient,
    Process launcher,
    TimeSpan timeout)
{
    var started = DateTimeOffset.UtcNow;
    var deadline = started + timeout;
    var lastFailure = "none";
    while (DateTimeOffset.UtcNow < deadline)
    {
        try
        {
            if (launcher.HasExited)
            {
                return new(null, DateTimeOffset.UtcNow - started, "launcher-exited");
            }
        }
        catch (InvalidOperationException)
        {
            return new(null, DateTimeOffset.UtcNow - started, "launcher-exited");
        }

        try
        {
            var info = await restClient.GetInfoAsync();
            return new(info, DateTimeOffset.UtcNow - started, "none");
        }
        catch (Exception exception) when (
            exception is HttpRequestException or
            InvalidOperationException or
            InvalidDataException or
            TaskCanceledException)
        {
            lastFailure = ClassifyRestFailure(exception);
            await Task.Delay(TimeSpan.FromSeconds(1));
        }
    }

    return new(null, DateTimeOffset.UtcNow - started, lastFailure);
}

static SortedDictionary<string, string> SnapshotSettings(JsonElement root)
{
    var result = new SortedDictionary<string, string>(StringComparer.Ordinal);
    if (root.ValueKind != JsonValueKind.Object)
    {
        return result;
    }

    foreach (var property in root.EnumerateObject())
    {
        // Never retain password-valued fields even if a future Palworld build adds one.
        if (property.Name.Contains("Password", StringComparison.OrdinalIgnoreCase))
        {
            continue;
        }

        result[property.Name] = property.Value.GetRawText();
    }

    return result;
}

static IReadOnlyList<string> GetChangedSettingNames(
    IReadOnlyDictionary<string, string> before,
    IReadOnlyDictionary<string, string> after)
    => before.Keys
        .Union(after.Keys, StringComparer.Ordinal)
        .Where(name => !before.TryGetValue(name, out var oldValue) ||
                       !after.TryGetValue(name, out var newValue) ||
                       !string.Equals(oldValue, newValue, StringComparison.Ordinal))
        .OrderBy(name => name, StringComparer.Ordinal)
        .ToArray();

static string SettingsFingerprint(IReadOnlyDictionary<string, string> settings)
{
    var canonical = string.Join(
        "\n",
        settings.Select(pair => pair.Key + "=" + pair.Value));
    return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
}

static string ClassifyRestFailure(Exception exception)
    => exception switch
    {
        HttpRequestException => "transport-or-http",
        TaskCanceledException => "timeout",
        InvalidDataException => "malformed-response",
        InvalidOperationException => "authentication-or-state",
        _ => "unknown"
    };

static bool IsAnyPalworldProcessRunning()
    => IsProcessRunning(LauncherProcessName) || IsProcessRunning(ShippingProcessName);

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
                // Process disappeared while being observed.
            }
        }
    }

    return false;
}

static async Task<bool> WaitForPalworldExitAsync(Process launcher, TimeSpan timeout)
{
    var deadline = DateTimeOffset.UtcNow + timeout;
    while (DateTimeOffset.UtcNow < deadline)
    {
        var launcherExited = false;
        try
        {
            launcherExited = launcher.HasExited;
        }
        catch (InvalidOperationException)
        {
            launcherExited = true;
        }

        if (launcherExited && !IsAnyPalworldProcessRunning())
        {
            return true;
        }

        await Task.Delay(TimeSpan.FromMilliseconds(250));
    }

    return !IsAnyPalworldProcessRunning();
}

static async Task<bool> ForceCleanupPalworldProcessesAsync()
{
    var killedAny = false;
    foreach (var processName in new[] { ShippingProcessName, LauncherProcessName })
    {
        foreach (var process in Process.GetProcessesByName(processName))
        {
            using (process)
            {
                try
                {
                    if (!process.HasExited)
                    {
                        process.Kill(entireProcessTree: true);
                        await process.WaitForExitAsync();
                        killedAny = true;
                    }
                }
                catch (InvalidOperationException)
                {
                    // Process disappeared before cleanup reached it.
                }
            }
        }
    }

    return killedAny;
}

static async Task WriteAtomicallyAsync(string destinationPath, byte[] bytes)
{
    var directory = Path.GetDirectoryName(destinationPath)
        ?? throw new InvalidOperationException("Could not determine the WorldOption directory.");
    var temporaryPath = Path.Combine(
        directory,
        $".{Path.GetFileName(destinationPath)}.sharedworlds-startup-restore-{Guid.NewGuid():N}.tmp");

    try
    {
        await File.WriteAllBytesAsync(temporaryPath, bytes);
        await using (var stream = new FileStream(
            temporaryPath,
            FileMode.Open,
            FileAccess.ReadWrite,
            FileShare.None,
            4096,
            FileOptions.WriteThrough))
        {
            await stream.FlushAsync();
            stream.Flush(flushToDisk: true);
        }

        File.Move(temporaryPath, destinationPath, overwrite: true);
    }
    finally
    {
        if (File.Exists(temporaryPath))
        {
            try
            {
                File.Delete(temporaryPath);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Best-effort cleanup of an acceptance-only temporary file.
            }
        }
    }
}

static string Sha256(ReadOnlySpan<byte> bytes)
    => Convert.ToHexString(SHA256.HashData(bytes));

static async Task<string> Sha256FileAsync(string path)
{
    await using var stream = new FileStream(
        path,
        FileMode.Open,
        FileAccess.Read,
        FileShare.ReadWrite | FileShare.Delete,
        128 * 1024,
        FileOptions.Asynchronous | FileOptions.SequentialScan);
    return Convert.ToHexString(await SHA256.HashDataAsync(stream));
}

sealed record ReadinessResult(PalworldServerInfo? Info, TimeSpan Elapsed, string LastFailure);
