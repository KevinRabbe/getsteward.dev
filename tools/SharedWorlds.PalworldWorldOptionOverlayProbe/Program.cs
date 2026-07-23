using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SharedWorlds.GameAdapters.Palworld;

const string PreservedWorldOptionSuffix = ".steward-rest-test";
const string ShippingProcessName = "PalServer-Win64-Shipping-Cmd";

Console.WriteLine("SharedWorlds Palworld WorldOption runtime-overlay acceptance");
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
            installation.Metadata.TryGetValue(
                PalworldInstallationDiscovery.DedicatedServerRootPathKey,
                out var root) &&
            !string.IsNullOrWhiteSpace(root) &&
            installation.Metadata.TryGetValue(
                PalworldInstallationDiscovery.DedicatedServerExecutablePathKey,
                out var executable) &&
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
    if (!configuration.ConfigExists || !configuration.RestEnabled || configuration.RestPort is null)
    {
        Console.Error.WriteLine("  blockingReason: REST must already be enabled with a valid port. This probe will not rewrite PalWorldSettings.ini.");
        return false;
    }

    var selectedWorldId = ReadSelectedWorldId(serverRoot);
    if (string.IsNullOrWhiteSpace(selectedWorldId))
    {
        Console.Error.WriteLine("DedicatedServerName was not found in GameUserSettings.ini.");
        return false;
    }

    var worlds = await adapter.DiscoverWorldsAsync(installation);
    var selectedWorld = worlds.SingleOrDefault(world =>
        world.Id.StartsWith("dedicated:", StringComparison.OrdinalIgnoreCase) &&
        string.Equals(
            Path.GetFileName(Path.TrimEndingDirectorySeparator(world.SourcePath)),
            selectedWorldId,
            StringComparison.OrdinalIgnoreCase));
    if (selectedWorld is null || !File.Exists(Path.Combine(selectedWorld.SourcePath, "Level.sav")))
    {
        Console.Error.WriteLine($"The selected dedicated World '{selectedWorldId}' was not discovered with a valid Level.sav.");
        return false;
    }

    if (IsAnyPalworldProcessRunning())
    {
        Console.Error.WriteLine("PalServer is already running. Stop it before the overlay acceptance probe.");
        return false;
    }

    var worldOptionPath = Path.Combine(selectedWorld.SourcePath, "WorldOption.sav");
    var preservedPath = worldOptionPath + PreservedWorldOptionSuffix;
    var originalSourcePath = ResolveOriginalWorldOption(worldOptionPath, preservedPath);
    if (originalSourcePath is null)
    {
        Console.Error.WriteLine("No canonical WorldOption.sav or preserved WorldOption.sav.steward-rest-test was found.");
        return false;
    }

    var originalBytes = await File.ReadAllBytesAsync(originalSourcePath);
    var originalHash = Sha256(originalBytes);
    if (File.Exists(worldOptionPath) && File.Exists(preservedPath))
    {
        var preservedHash = await Sha256FileAsync(preservedPath);
        var activeHash = await Sha256FileAsync(worldOptionPath);
        if (!string.Equals(preservedHash, activeHash, StringComparison.Ordinal))
        {
            Console.Error.WriteLine("WorldOption.sav and its preserved acceptance backup differ; refusing to choose one automatically.");
            return false;
        }
    }

    var transientPassword = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    using var transientCredential = new SecretBytes(transientPassword);
    PalworldWorldOptionOverlayResult overlay;
    try
    {
        overlay = PalworldWorldOptionAdminPasswordOverlay.Create(originalBytes, transientPassword);
    }
    catch (InvalidDataException exception)
    {
        Console.Error.WriteLine($"WorldOption overlay validation failed: {exception.Message}");
        return false;
    }

    Console.WriteLine();
    Console.WriteLine("overlay:");
    Console.WriteLine($"  worldId: {selectedWorldId}");
    Console.WriteLine($"  sourcePath: {originalSourcePath}");
    Console.WriteLine($"  sourceWasPreservedAcceptanceBackup: {string.Equals(originalSourcePath, preservedPath, StringComparison.OrdinalIgnoreCase)}");
    Console.WriteLine($"  saveType: 0x{overlay.SaveType:X2}");
    Console.WriteLine($"  existingAdminPasswordConfigured: {overlay.ExistingAdminPasswordConfigured}");
    Console.WriteLine($"  originalSha256: {overlay.OriginalSaveSha256}");
    Console.WriteLine($"  patchedSha256: {overlay.PatchedSaveSha256}");
    Console.WriteLine($"  originalPayloadBytes: {overlay.OriginalPayloadLength}");
    Console.WriteLine($"  patchedPayloadBytes: {overlay.PatchedPayloadLength}");
    Console.WriteLine("  byteExactRevertSelfCheck: true");
    Console.WriteLine("  transientPasswordPrinted: false");

    Process? launcher = null;
    var runtimeFileWritten = false;
    var forcedCleanupUsed = false;
    var restReady = false;
    var worldGuidMatched = false;
    var settingsReadable = false;
    var saveSucceeded = false;
    var shutdownSucceeded = false;
    var processesExited = false;
    var runtimeMutatedByServer = false;
    var originalRestored = false;
    var credentialPresentAfterRestore = true;
    var captureCreated = false;
    var capturedWorldOptionMatchesOriginal = false;
    var capturedPackageContainsCredential = true;
    var captureContainsPreservedTestBackup = true;
    string? capturePackagePath = null;

    try
    {
        await WriteAtomicallyAsync(worldOptionPath, overlay.PatchedSave);
        runtimeFileWritten = true;
        var writtenHash = await Sha256FileAsync(worldOptionPath);
        if (!string.Equals(writtenHash, overlay.PatchedSaveSha256, StringComparison.Ordinal))
        {
            Console.Error.WriteLine("The temporary WorldOption overlay did not persist byte-for-byte before launch.");
            return false;
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
        var restClient = new PalworldRestApiClient(httpClient, "admin", transientPassword);
        var readinessStartedAt = DateTimeOffset.UtcNow;
        PalworldServerInfo? info = null;
        var readinessDeadline = readinessStartedAt + TimeSpan.FromSeconds(90);
        while (DateTimeOffset.UtcNow < readinessDeadline)
        {
            if (launcher.HasExited)
            {
                break;
            }

            try
            {
                info = await restClient.GetInfoAsync();
                break;
            }
            catch (Exception exception) when (
                exception is HttpRequestException or
                InvalidOperationException or
                InvalidDataException or
                TaskCanceledException)
            {
                await Task.Delay(TimeSpan.FromSeconds(1));
            }
        }

        restReady = info is not null;
        Console.WriteLine("readiness:");
        Console.WriteLine($"  success: {restReady}");
        Console.WriteLine($"  elapsed: {DateTimeOffset.UtcNow - readinessStartedAt}");
        if (!restReady)
        {
            Console.Error.WriteLine("  blockingReason: authenticated REST /info did not become ready.");
            return false;
        }

        worldGuidMatched = string.Equals(info!.WorldGuid, selectedWorldId, StringComparison.OrdinalIgnoreCase);
        Console.WriteLine($"  version: {info.Version}");
        Console.WriteLine($"  serverName: {info.ServerName}");
        Console.WriteLine($"  worldGuid: {info.WorldGuid}");
        Console.WriteLine($"  worldGuidMatched: {worldGuidMatched}");
        if (!worldGuidMatched)
        {
            Console.Error.WriteLine("  blockingReason: REST reported a different active World.");
            return false;
        }

        using (var settings = await restClient.GetSettingsAsync())
        {
            settingsReadable = settings.RootElement.ValueKind == JsonValueKind.Object;
            Console.WriteLine("settings:");
            Console.WriteLine($"  readable: {settingsReadable}");
            if (settingsReadable)
            {
                Console.WriteLine($"  propertyCount: {settings.RootElement.EnumerateObject().Count()}");
                PrintSafeJsonProperty(settings.RootElement, "ServerName");
                PrintSafeJsonProperty(settings.RootElement, "RESTAPIEnabled");
                PrintSafeJsonProperty(settings.RootElement, "RESTAPIPort");
                PrintSafeJsonProperty(settings.RootElement, "BaseCampWorkerMaxNum");
            }
        }

        var hashAfterReadiness = await Sha256FileAsync(worldOptionPath);
        runtimeMutatedByServer |= !string.Equals(hashAfterReadiness, overlay.PatchedSaveSha256, StringComparison.Ordinal);
        Console.WriteLine("worldOptionHashTimeline:");
        Console.WriteLine($"  beforeLaunch: {overlay.PatchedSaveSha256}");
        Console.WriteLine($"  afterReadiness: {hashAfterReadiness}");

        await restClient.SaveAsync();
        saveSucceeded = true;
        var hashAfterSave = await Sha256FileAsync(worldOptionPath);
        runtimeMutatedByServer |= !string.Equals(hashAfterSave, overlay.PatchedSaveSha256, StringComparison.Ordinal);
        Console.WriteLine($"  afterSave: {hashAfterSave}");

        await restClient.ShutdownAsync(1, "Steward WorldOption overlay acceptance shutdown.");
        shutdownSucceeded = true;
        processesExited = await WaitForPalworldExitAsync(launcher, TimeSpan.FromSeconds(60));
        if (!processesExited)
        {
            Console.Error.WriteLine("Palworld did not fully exit within the bounded graceful-shutdown window.");
            return false;
        }

        var hashAfterExit = await Sha256FileAsync(worldOptionPath);
        runtimeMutatedByServer |= !string.Equals(hashAfterExit, overlay.PatchedSaveSha256, StringComparison.Ordinal);
        Console.WriteLine($"  afterProcessExit: {hashAfterExit}");
        Console.WriteLine($"  mutatedByPalworldDuringSession: {runtimeMutatedByServer}");
    }
    catch (Exception exception) when (
        exception is IOException or
        UnauthorizedAccessException or
        HttpRequestException or
        InvalidOperationException or
        InvalidDataException or
        TaskCanceledException)
    {
        Console.Error.WriteLine($"Acceptance operation failed: {exception.Message}");
    }
    finally
    {
        if (launcher is not null)
        {
            try
            {
                if (!launcher.HasExited || IsAnyPalworldProcessRunning())
                {
                    launcher.Kill(entireProcessTree: true);
                    await launcher.WaitForExitAsync();
                    forcedCleanupUsed = true;
                }
            }
            catch (InvalidOperationException)
            {
                // It exited between the observation and cleanup.
            }
            finally
            {
                launcher.Dispose();
            }
        }

        if (runtimeFileWritten)
        {
            try
            {
                await WriteAtomicallyAsync(worldOptionPath, originalBytes);
                var restoredHash = await Sha256FileAsync(worldOptionPath);
                originalRestored = string.Equals(restoredHash, originalHash, StringComparison.Ordinal);
                var restoredBytes = await File.ReadAllBytesAsync(worldOptionPath);
                credentialPresentAfterRestore = ContainsSequence(
                    restoredBytes,
                    transientCredential.Bytes);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                Console.Error.WriteLine($"CRITICAL: original WorldOption restoration failed: {exception.Message}");
            }
        }

        transientPassword = string.Empty;
    }

    if (restReady &&
        worldGuidMatched &&
        settingsReadable &&
        saveSucceeded &&
        shutdownSucceeded &&
        processesExited &&
        originalRestored &&
        !credentialPresentAfterRestore &&
        !forcedCleanupUsed)
    {
        string? parkedPreservedBackup = null;
        try
        {
            if (File.Exists(preservedPath))
            {
                var preservedHash = await Sha256FileAsync(preservedPath);
                if (!string.Equals(preservedHash, originalHash, StringComparison.Ordinal))
                {
                    Console.Error.WriteLine("The preserved WorldOption acceptance backup no longer matches the canonical original.");
                }
                else
                {
                    var parkingRoot = Path.Combine(
                        Path.GetTempPath(),
                        "SharedWorlds",
                        "palworld-worldoption-overlay",
                        Guid.NewGuid().ToString("N"));
                    Directory.CreateDirectory(parkingRoot);
                    parkedPreservedBackup = Path.Combine(parkingRoot, Path.GetFileName(preservedPath));
                    File.Move(preservedPath, parkedPreservedBackup);
                }
            }

            var captured = await adapter.CaptureDetectedWorldAsync(installation, selectedWorld);
            capturePackagePath = captured.Package.Path;
            var inspection = await InspectCaptureAsync(
                capturePackagePath,
                originalHash,
                transientCredential.Bytes);
            captureCreated = true;
            capturedWorldOptionMatchesOriginal = inspection.WorldOptionMatchesOriginal;
            capturedPackageContainsCredential = inspection.ContainsCredential;
            captureContainsPreservedTestBackup = inspection.ContainsPreservedTestBackup;
        }
        catch (Exception exception) when (
            exception is IOException or
            UnauthorizedAccessException or
            InvalidDataException or
            InvalidOperationException)
        {
            Console.Error.WriteLine($"Post-overlay canonical capture verification failed: {exception.Message}");
        }
        finally
        {
            if (!string.IsNullOrWhiteSpace(parkedPreservedBackup) && File.Exists(parkedPreservedBackup))
            {
                try
                {
                    File.Move(parkedPreservedBackup, preservedPath);
                    var parkingDirectory = Path.GetDirectoryName(parkedPreservedBackup);
                    if (!string.IsNullOrWhiteSpace(parkingDirectory) && Directory.Exists(parkingDirectory))
                    {
                        Directory.Delete(parkingDirectory, recursive: true);
                    }
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    Console.Error.WriteLine($"Could not restore the preserved acceptance backup to its original path: {exception.Message}");
                }
            }

            if (!string.IsNullOrWhiteSpace(capturePackagePath) && File.Exists(capturePackagePath))
            {
                try
                {
                    File.Delete(capturePackagePath);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    Console.Error.WriteLine($"Could not delete the temporary acceptance package: {exception.Message}");
                }
            }
        }
    }

    var runtimeManagementOverlayProven =
        restReady &&
        worldGuidMatched &&
        settingsReadable &&
        saveSucceeded &&
        shutdownSucceeded &&
        processesExited &&
        originalRestored &&
        !credentialPresentAfterRestore &&
        !forcedCleanupUsed &&
        captureCreated &&
        capturedWorldOptionMatchesOriginal &&
        !capturedPackageContainsCredential &&
        !captureContainsPreservedTestBackup;

    Console.WriteLine();
    Console.WriteLine("result:");
    Console.WriteLine($"  restReady: {restReady}");
    Console.WriteLine($"  worldGuidMatched: {worldGuidMatched}");
    Console.WriteLine($"  settingsReadable: {settingsReadable}");
    Console.WriteLine($"  saveSucceeded: {saveSucceeded}");
    Console.WriteLine($"  shutdownSucceeded: {shutdownSucceeded}");
    Console.WriteLine($"  allPalworldProcessesExited: {processesExited}");
    Console.WriteLine($"  worldOptionMutatedByServer: {runtimeMutatedByServer}");
    Console.WriteLine($"  originalWorldOptionRestored: {originalRestored}");
    Console.WriteLine($"  transientCredentialPresentAfterRestore: {credentialPresentAfterRestore}");
    Console.WriteLine($"  canonicalCaptureCreated: {captureCreated}");
    Console.WriteLine($"  capturedWorldOptionMatchesOriginal: {capturedWorldOptionMatchesOriginal}");
    Console.WriteLine($"  capturedPackageContainsTransientCredential: {capturedPackageContainsCredential}");
    Console.WriteLine($"  captureContainsPreservedTestBackup: {captureContainsPreservedTestBackup}");
    Console.WriteLine($"  forcedCleanupUsed: {forcedCleanupUsed}");
    Console.WriteLine($"runtimeManagementOverlayProven: {runtimeManagementOverlayProven}");
    Console.WriteLine($"preservedAcceptanceBackupStillPresent: {File.Exists(preservedPath)}");

    return runtimeManagementOverlayProven;
}

static string? ResolveOriginalWorldOption(string worldOptionPath, string preservedPath)
{
    if (File.Exists(worldOptionPath))
    {
        return worldOptionPath;
    }

    return File.Exists(preservedPath) ? preservedPath : null;
}

static string? ReadSelectedWorldId(string serverRoot)
{
    var path = Path.Combine(
        serverRoot,
        "Pal",
        "Saved",
        "Config",
        "WindowsServer",
        "GameUserSettings.ini");
    if (!File.Exists(path))
    {
        return null;
    }

    foreach (var rawLine in File.ReadLines(path))
    {
        var line = rawLine.Trim();
        const string prefix = "DedicatedServerName=";
        if (line.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            var value = line[prefix.Length..].Trim();
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }
    }

    return null;
}

static bool IsAnyPalworldProcessRunning()
    => IsProcessRunning("PalServer") || IsProcessRunning(ShippingProcessName);

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
                // The process disappeared while being inspected.
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

static async Task WriteAtomicallyAsync(string destinationPath, byte[] bytes)
{
    var directory = Path.GetDirectoryName(destinationPath)
        ?? throw new InvalidOperationException("Could not determine the WorldOption directory.");
    Directory.CreateDirectory(directory);
    var temporaryPath = Path.Combine(
        directory,
        $".{Path.GetFileName(destinationPath)}.sharedworlds-overlay-{Guid.NewGuid():N}.tmp");

    try
    {
        await File.WriteAllBytesAsync(temporaryPath, bytes);
        await using (var stream = new FileStream(
            temporaryPath,
            FileMode.Open,
            FileAccess.ReadWrite,
            FileShare.None,
            bufferSize: 4096,
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
            catch (IOException)
            {
                // Best-effort cleanup of a failed atomic write.
            }
            catch (UnauthorizedAccessException)
            {
                // Best-effort cleanup of a failed atomic write.
            }
        }
    }
}

static async Task<CaptureInspection> InspectCaptureAsync(
    string packagePath,
    string expectedWorldOptionSha256,
    byte[] transientCredential)
{
    using var archive = ZipFile.OpenRead(packagePath);
    var worldOptionEntry = archive.Entries.SingleOrDefault(entry =>
        string.Equals(
            entry.FullName.Replace('\\', '/'),
            "WorldOption.sav",
            StringComparison.OrdinalIgnoreCase));
    if (worldOptionEntry is null)
    {
        throw new InvalidDataException("The canonical package does not contain WorldOption.sav.");
    }

    string worldOptionHash;
    await using (var stream = worldOptionEntry.Open())
    {
        worldOptionHash = Convert.ToHexString(await SHA256.HashDataAsync(stream));
    }

    var containsPreservedTestBackup = archive.Entries.Any(entry =>
        entry.FullName.Contains(PreservedWorldOptionSuffix, StringComparison.OrdinalIgnoreCase));

    var containsCredential = false;
    foreach (var entry in archive.Entries.Where(entry => !string.IsNullOrEmpty(entry.Name)))
    {
        await using var stream = entry.Open();
        if (await StreamContainsAsync(stream, transientCredential))
        {
            containsCredential = true;
            break;
        }
    }

    return new CaptureInspection(
        string.Equals(worldOptionHash, expectedWorldOptionSha256, StringComparison.Ordinal),
        containsCredential,
        containsPreservedTestBackup);
}

static async Task<bool> StreamContainsAsync(Stream stream, byte[] needle)
{
    if (needle.Length == 0)
    {
        return true;
    }

    var buffer = new byte[64 * 1024 + needle.Length - 1];
    var retained = 0;
    while (true)
    {
        var read = await stream.ReadAsync(buffer.AsMemory(retained, 64 * 1024));
        if (read == 0)
        {
            return retained >= needle.Length &&
                buffer.AsSpan(0, retained).IndexOf(needle) >= 0;
        }

        var available = retained + read;
        if (buffer.AsSpan(0, available).IndexOf(needle) >= 0)
        {
            return true;
        }

        retained = Math.Min(needle.Length - 1, available);
        buffer.AsSpan(available - retained, retained).CopyTo(buffer);
    }
}

static void PrintSafeJsonProperty(JsonElement root, string propertyName)
{
    if (propertyName.Contains("Password", StringComparison.OrdinalIgnoreCase))
    {
        throw new InvalidOperationException("Password-valued REST settings must never be printed by the acceptance probe.");
    }

    if (root.TryGetProperty(propertyName, out var value))
    {
        Console.WriteLine($"  {propertyName}: {value}");
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
        FileShare.Read,
        bufferSize: 128 * 1024,
        FileOptions.Asynchronous | FileOptions.SequentialScan);
    return Convert.ToHexString(await SHA256.HashDataAsync(stream));
}

static bool ContainsSequence(ReadOnlySpan<byte> haystack, ReadOnlySpan<byte> needle)
    => needle.Length == 0 || haystack.IndexOf(needle) >= 0;

sealed class SecretBytes : IDisposable
{
    public SecretBytes(string value)
    {
        Bytes = Encoding.ASCII.GetBytes(value);
    }

    public byte[] Bytes { get; }

    public void Dispose()
    {
        CryptographicOperations.ZeroMemory(Bytes);
    }
}

sealed record CaptureInspection(
    bool WorldOptionMatchesOriginal,
    bool ContainsCredential,
    bool ContainsPreservedTestBackup);
