using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SharedWorlds.GameAdapters.Palworld;

const string LauncherProcessName = "PalServer";
const string ShippingProcessName = "PalServer-Win64-Shipping-Cmd";

Console.WriteLine("SharedWorlds Palworld managed-session runtime-input lifecycle acceptance");
Console.WriteLine();

if (!OperatingSystem.IsWindows())
{
    Console.Error.WriteLine("This acceptance probe requires the real Windows Palworld dedicated server.");
    Environment.ExitCode = 2;
    return;
}

Environment.ExitCode = await RunAsync() ? 0 : 2;

static async Task<bool> RunAsync()
{
    if (IsAnyPalworldProcessRunning())
    {
        Console.Error.WriteLine("PalServer is already running. Stop it before this acceptance probe.");
        return false;
    }

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
        Console.Error.WriteLine("PalServer.exe was not found at the discovered path.");
        return false;
    }

    var configuration = PalworldRestAcceptanceConfigurationReader.Read(serverRoot);
    if (!configuration.ConfigExists ||
        configuration.RestPort is null ||
        configuration.RestPort is < 1 or > 65535 ||
        string.IsNullOrWhiteSpace(configuration.SelectedWorldId))
    {
        Console.Error.WriteLine(
            "The existing PalWorldSettings.ini must exist, have a valid REST port, and GameUserSettings.ini must select a World.");
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
        Console.Error.WriteLine("The selected dedicated World was not discovered with a valid Level.sav.");
        return false;
    }

    var worldOptionPath = Path.Combine(selectedWorld.SourcePath, "WorldOption.sav");
    var iniPath = configuration.ConfigPath;
    if (!File.Exists(worldOptionPath) || !File.Exists(iniPath))
    {
        Console.Error.WriteLine("The canonical WorldOption.sav or PalWorldSettings.ini is missing.");
        return false;
    }

    byte[] originalWorldOptionBytes;
    byte[] originalIniBytes;
    try
    {
        originalWorldOptionBytes = await File.ReadAllBytesAsync(worldOptionPath);
        originalIniBytes = await File.ReadAllBytesAsync(iniPath);
    }
    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
    {
        Console.Error.WriteLine($"Canonical runtime inputs could not be read: {exception.Message}");
        return false;
    }

    var originalWorldOptionHash = Sha256(originalWorldOptionBytes);
    var originalIniHash = Sha256(originalIniBytes);
    var parkingPath = worldOptionPath + $".sharedworlds-runtime-session-{Guid.NewGuid():N}";
    var transientPassword = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));

    PalworldOodleCodec? oodleCodec = null;
    Process? launcher = null;
    var parked = false;
    var tempIniInstalled = false;
    var readinessSucceeded = false;
    var worldGuidMatched = false;
    var restSettingsReadable = false;
    var saveSucceeded = false;
    var shutdownSucceeded = false;
    var allProcessesExited = false;
    var forcedCleanupUsed = false;
    var temporaryIniMutatedByPalworld = false;
    var unexpectedWorldOptionGenerated = false;
    var originalsRestoredAfterProcessExit = false;
    var finalWorldOptionHashMatches = false;
    var finalIniHashMatches = false;
    var transientCredentialAbsentFromRestoredFiles = false;
    string? temporaryIniHash = null;

    try
    {
        if (PalworldWorldOptionAdminPasswordRuntimeOverlay.RequiresOodle(originalWorldOptionBytes))
        {
            oodleCodec = PalworldOodleCodec.LoadFromPalworldRoots(serverRoot, installation.RootPath);
            Console.WriteLine($"oodleLibrary: {oodleCodec.LibraryPath}");
            Console.WriteLine("oodleUsage: decode-only");
        }

        var snapshot = PalworldWorldOptionSettingsReader.Read(originalWorldOptionBytes, oodleCodec);
        var mirror = PalworldWorldOptionIniMirror.Create(
            snapshot,
            transientPassword,
            configuration.RestPort.Value);
        var temporaryIniBytes = Encoding.UTF8.GetBytes(mirror.Contents);
        temporaryIniHash = Sha256(temporaryIniBytes);

        Console.WriteLine("preflight:");
        Console.WriteLine($"  worldId: {configuration.SelectedWorldId}");
        Console.WriteLine($"  container: {snapshot.Container}");
        Console.WriteLine($"  extractedSettings: {snapshot.Settings.Count}");
        Console.WriteLine($"  serializedSettings: {mirror.SerializedValues.Count}");
        Console.WriteLine($"  worldOptionSha256: {originalWorldOptionHash}");
        Console.WriteLine($"  originalIniSha256: {originalIniHash}");
        Console.WriteLine($"  temporaryIniSha256: {temporaryIniHash}");

        await WriteAtomicallyAsync(iniPath, temporaryIniBytes);
        tempIniInstalled = true;

        var mirroredConfig = PalworldRestAcceptanceConfigurationReader.Read(serverRoot);
        if (!mirroredConfig.ConfigExists ||
            !mirroredConfig.RestEnabled ||
            mirroredConfig.RestPort != configuration.RestPort ||
            !mirroredConfig.AdminPasswordConfigured ||
            !string.Equals(mirroredConfig.SelectedWorldId, configuration.SelectedWorldId, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Generated PalWorldSettings.ini did not pass Steward's configuration preflight.");
        }

        var loadedPassword = PalworldRestAcceptanceConfigurationReader.ReadAdminPassword(serverRoot);
        if (!string.Equals(loadedPassword, transientPassword, StringComparison.Ordinal))
        {
            loadedPassword = string.Empty;
            throw new InvalidDataException("Generated PalWorldSettings.ini did not round-trip the transient AdminPassword.");
        }
        loadedPassword = string.Empty;

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
            throw new InvalidOperationException("PalServer.exe could not be started.");
        }

        using var httpClient = new HttpClient
        {
            BaseAddress = new Uri($"http://127.0.0.1:{configuration.RestPort.Value}/", UriKind.Absolute),
            Timeout = TimeSpan.FromSeconds(5)
        };
        var restClient = new PalworldRestApiClient(httpClient, "admin", transientPassword);

        var readiness = await WaitForInfoAsync(restClient, launcher, TimeSpan.FromSeconds(90));
        readinessSucceeded = readiness.Info is not null;
        Console.WriteLine();
        Console.WriteLine("readiness:");
        Console.WriteLine($"  success: {readinessSucceeded}");
        Console.WriteLine($"  elapsed: {readiness.Elapsed}");
        Console.WriteLine($"  lastFailure: {readiness.LastFailure}");
        if (!readinessSucceeded)
        {
            throw new InvalidOperationException("Authenticated REST /info did not become ready from the mirrored INI.");
        }

        worldGuidMatched = string.Equals(
            readiness.Info!.WorldGuid,
            configuration.SelectedWorldId,
            StringComparison.OrdinalIgnoreCase);
        Console.WriteLine($"  worldGuidMatched: {worldGuidMatched}");
        if (!worldGuidMatched)
        {
            throw new InvalidDataException("REST reported a different active World.");
        }

        using (var settings = await restClient.GetSettingsAsync())
        {
            restSettingsReadable = settings.RootElement.ValueKind == JsonValueKind.Object &&
                settings.RootElement.EnumerateObject().Any();
        }
        if (!restSettingsReadable)
        {
            throw new InvalidDataException("Palworld /settings was not readable during the managed session.");
        }

        await restClient.SaveAsync();
        saveSucceeded = true;

        await restClient.ShutdownAsync(1, "Steward runtime-input lifecycle acceptance shutdown.");
        shutdownSucceeded = true;
        allProcessesExited = await WaitForPalworldExitAsync(launcher, TimeSpan.FromSeconds(60));

        Console.WriteLine();
        Console.WriteLine("shutdownBoundary:");
        Console.WriteLine($"  saveSucceeded: {saveSucceeded}");
        Console.WriteLine($"  shutdownSucceeded: {shutdownSucceeded}");
        Console.WriteLine($"  allPalworldProcessesExited: {allProcessesExited}");
        if (!allProcessesExited)
        {
            throw new InvalidOperationException("Palworld process tree did not fully exit before the restoration boundary.");
        }

        unexpectedWorldOptionGenerated = File.Exists(worldOptionPath);
        temporaryIniMutatedByPalworld = File.Exists(iniPath) &&
            !string.Equals(await Sha256FileAsync(iniPath), temporaryIniHash, StringComparison.Ordinal);

        Console.WriteLine($"  unexpectedWorldOptionGenerated: {unexpectedWorldOptionGenerated}");
        Console.WriteLine($"  temporaryIniMutatedByPalworld: {temporaryIniMutatedByPalworld}");

        if (unexpectedWorldOptionGenerated)
        {
            throw new InvalidDataException(
                "Palworld generated WorldOption.sav while the canonical file was parked; restoration will recover the canonical file, but this lifecycle is not accepted.");
        }

        File.Move(parkingPath, worldOptionPath);
        parked = false;
        await WriteAtomicallyAsync(iniPath, originalIniBytes);
        tempIniInstalled = false;
        originalsRestoredAfterProcessExit = true;

        finalWorldOptionHashMatches = string.Equals(
            await Sha256FileAsync(worldOptionPath),
            originalWorldOptionHash,
            StringComparison.Ordinal);
        finalIniHashMatches = string.Equals(
            await Sha256FileAsync(iniPath),
            originalIniHash,
            StringComparison.Ordinal);

        var credentialBytes = Encoding.UTF8.GetBytes(transientPassword);
        try
        {
            transientCredentialAbsentFromRestoredFiles =
                !ContainsSequence(await File.ReadAllBytesAsync(worldOptionPath), credentialBytes) &&
                !ContainsSequence(await File.ReadAllBytesAsync(iniPath), credentialBytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(credentialBytes);
        }
    }
    catch (Exception exception) when (
        exception is IOException or
        UnauthorizedAccessException or
        HttpRequestException or
        InvalidOperationException or
        InvalidDataException or
        TaskCanceledException or
        DllNotFoundException or
        BadImageFormatException or
        EntryPointNotFoundException or
        ArgumentException or
        OverflowException)
    {
        Console.Error.WriteLine($"Runtime-input lifecycle acceptance failed: {exception.Message}");
    }
    finally
    {
        oodleCodec?.Dispose();

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
                // Process exited while cleanup state was being observed.
            }
            finally
            {
                launcher.Dispose();
            }
        }

        try
        {
            if (parked && File.Exists(parkingPath))
            {
                if (File.Exists(worldOptionPath))
                {
                    File.Delete(worldOptionPath);
                }

                File.Move(parkingPath, worldOptionPath);
                parked = false;
            }
            else if (!File.Exists(worldOptionPath))
            {
                await WriteAtomicallyAsync(worldOptionPath, originalWorldOptionBytes);
            }

            if (tempIniInstalled || !File.Exists(iniPath))
            {
                await WriteAtomicallyAsync(iniPath, originalIniBytes);
                tempIniInstalled = false;
            }

            finalWorldOptionHashMatches = File.Exists(worldOptionPath) &&
                string.Equals(await Sha256FileAsync(worldOptionPath), originalWorldOptionHash, StringComparison.Ordinal);
            finalIniHashMatches = File.Exists(iniPath) &&
                string.Equals(await Sha256FileAsync(iniPath), originalIniHash, StringComparison.Ordinal);

            var credentialBytes = Encoding.UTF8.GetBytes(transientPassword);
            try
            {
                transientCredentialAbsentFromRestoredFiles =
                    !ContainsSequence(await File.ReadAllBytesAsync(worldOptionPath), credentialBytes) &&
                    !ContainsSequence(await File.ReadAllBytesAsync(iniPath), credentialBytes);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(credentialBytes);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"CRITICAL: final canonical input restoration failed: {exception.Message}");
        }

        CryptographicOperations.ZeroMemory(originalWorldOptionBytes);
        CryptographicOperations.ZeroMemory(originalIniBytes);
        transientPassword = string.Empty;
    }

    var proven = readinessSucceeded &&
        worldGuidMatched &&
        restSettingsReadable &&
        saveSucceeded &&
        shutdownSucceeded &&
        allProcessesExited &&
        !forcedCleanupUsed &&
        !unexpectedWorldOptionGenerated &&
        originalsRestoredAfterProcessExit &&
        finalWorldOptionHashMatches &&
        finalIniHashMatches &&
        transientCredentialAbsentFromRestoredFiles;

    Console.WriteLine();
    Console.WriteLine("result:");
    Console.WriteLine($"  readinessSucceeded: {readinessSucceeded}");
    Console.WriteLine($"  worldGuidMatched: {worldGuidMatched}");
    Console.WriteLine($"  restSettingsReadable: {restSettingsReadable}");
    Console.WriteLine($"  saveSucceeded: {saveSucceeded}");
    Console.WriteLine($"  shutdownSucceeded: {shutdownSucceeded}");
    Console.WriteLine($"  allPalworldProcessesExited: {allProcessesExited}");
    Console.WriteLine($"  temporaryIniMutatedByPalworld: {temporaryIniMutatedByPalworld}");
    Console.WriteLine($"  unexpectedWorldOptionGenerated: {unexpectedWorldOptionGenerated}");
    Console.WriteLine($"  originalsRestoredAfterProcessExit: {originalsRestoredAfterProcessExit}");
    Console.WriteLine($"  forcedCleanupUsed: {forcedCleanupUsed}");
    Console.WriteLine($"  finalWorldOptionSha256Matches: {finalWorldOptionHashMatches}");
    Console.WriteLine($"  finalPalWorldSettingsIniSha256Matches: {finalIniHashMatches}");
    Console.WriteLine($"  transientCredentialAbsentFromRestoredFiles: {transientCredentialAbsentFromRestoredFiles}");
    Console.WriteLine($"postExitRuntimeInputRestorationProven: {proven}");

    return proven;
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
        if (launcher.HasExited && !IsAnyPalworldProcessRunning())
        {
            return new ReadinessResult(null, DateTimeOffset.UtcNow - started, "Palworld process tree exited before readiness");
        }

        try
        {
            return new ReadinessResult(
                await restClient.GetInfoAsync(),
                DateTimeOffset.UtcNow - started,
                "none");
        }
        catch (Exception exception) when (
            exception is HttpRequestException or
            InvalidOperationException or
            InvalidDataException or
            TaskCanceledException)
        {
            lastFailure = exception.GetType().Name;
        }

        await Task.Delay(500);
    }

    return new ReadinessResult(null, DateTimeOffset.UtcNow - started, lastFailure);
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

        await Task.Delay(250);
    }

    return false;
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
                        killedAny = true;
                        await process.WaitForExitAsync();
                    }
                }
                catch (Exception exception) when (
                    exception is InvalidOperationException or
                    System.ComponentModel.Win32Exception)
                {
                    // Process disappeared or could not be killed; final observation decides safety.
                }
            }
        }
    }

    return killedAny;
}

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

static async Task WriteAtomicallyAsync(string path, byte[] bytes)
{
    var directory = Path.GetDirectoryName(path) ?? throw new InvalidOperationException("Path has no parent directory.");
    Directory.CreateDirectory(directory);
    var temp = Path.Combine(directory, $".{Path.GetFileName(path)}.sharedworlds-{Guid.NewGuid():N}.tmp");
    try
    {
        await File.WriteAllBytesAsync(temp, bytes);
        File.Move(temp, path, overwrite: true);
    }
    finally
    {
        if (File.Exists(temp))
        {
            File.Delete(temp);
        }
    }
}

static string Sha256(ReadOnlySpan<byte> bytes)
    => Convert.ToHexString(SHA256.HashData(bytes));

static async Task<string> Sha256FileAsync(string path)
    => Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(path)));

static bool ContainsSequence(ReadOnlySpan<byte> haystack, ReadOnlySpan<byte> needle)
    => !needle.IsEmpty && haystack.IndexOf(needle) >= 0;

internal sealed record ReadinessResult(
    PalworldServerInfo? Info,
    TimeSpan Elapsed,
    string LastFailure);
