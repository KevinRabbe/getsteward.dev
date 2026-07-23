using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SharedWorlds.GameAdapters.Palworld;

const string LauncherProcessName = "PalServer";
const string ShippingProcessName = "PalServer-Win64-Shipping-Cmd";

Console.WriteLine("SharedWorlds Palworld WorldOption -> PalWorldSettings.ini mirror acceptance");
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
    if (!File.Exists(worldOptionPath))
    {
        Console.Error.WriteLine("The selected World does not contain WorldOption.sav.");
        return false;
    }

    var iniPath = configuration.ConfigPath;
    if (!File.Exists(iniPath))
    {
        Console.Error.WriteLine("The current PalWorldSettings.ini disappeared after discovery.");
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
    var parkingPath = worldOptionPath + $".sharedworlds-ini-mirror-{Guid.NewGuid():N}";
    var transientPassword = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));

    PalworldOodleCodec? oodleCodec = null;
    Process? launcher = null;
    var parked = false;
    var tempIniInstalled = false;
    var liveWorldOptionCopyRestored = false;
    var originalIniRestoredLive = false;
    var readinessSucceeded = false;
    var worldGuidMatched = false;
    var restSettingsReadable = false;
    var restExposedSettingsMatch = false;
    var restSettingsStableAfterRestore = false;
    var shutdownSucceeded = false;
    var allProcessesExited = false;
    var forcedCleanupUsed = false;
    var liveWorldOptionMutated = false;
    var liveIniMutated = false;
    var finalWorldOptionHashMatches = false;
    var finalIniHashMatches = false;
    var transientCredentialAbsentFromRestoredFiles = false;
    var verifiedSettingCount = 0;
    var unexposedSettingNames = Array.Empty<string>();
    var restOnlySettingNames = Array.Empty<string>();
    var mismatchNames = Array.Empty<string>();

    try
    {
        if (PalworldWorldOptionAdminPasswordRuntimeOverlay.RequiresOodle(originalWorldOptionBytes))
        {
            oodleCodec = PalworldOodleCodec.LoadFromPalworldRoots(serverRoot, installation.RootPath);
            Console.WriteLine($"oodleLibrary: {oodleCodec.LibraryPath}");
            Console.WriteLine("oodleUsage: decode-only");
        }

        PalworldWorldOptionSettingsSnapshot snapshot;
        try
        {
            snapshot = PalworldWorldOptionSettingsReader.Read(originalWorldOptionBytes, oodleCodec);
        }
        catch (Exception exception) when (
            exception is InvalidDataException or
            InvalidOperationException or
            ArgumentException or
            OverflowException)
        {
            Console.Error.WriteLine($"WorldOption settings extraction failed closed: {exception.Message}");
            return false;
        }

        PalworldWorldOptionIniMirrorResult mirror;
        try
        {
            mirror = PalworldWorldOptionIniMirror.Create(
                snapshot,
                transientPassword,
                configuration.RestPort.Value);
        }
        catch (Exception exception) when (
            exception is InvalidDataException or
            ArgumentException or
            ArgumentOutOfRangeException or
            OverflowException)
        {
            Console.Error.WriteLine($"INI mirror preflight failed closed before any file mutation: {exception.Message}");
            return false;
        }

        Console.WriteLine("preflight:");
        Console.WriteLine($"  worldId: {configuration.SelectedWorldId}");
        Console.WriteLine($"  container: {snapshot.Container}");
        Console.WriteLine($"  extractedSettings: {snapshot.Settings.Count}");
        Console.WriteLine($"  serializedSettings: {mirror.SerializedValues.Count}");
        Console.WriteLine($"  managementOverrides: {string.Join(", ", mirror.ManagementOverrides.OrderBy(name => name, StringComparer.Ordinal))}");
        Console.WriteLine($"  worldOptionSha256: {originalWorldOptionHash}");
        Console.WriteLine($"  originalIniSha256: {originalIniHash}");

        await WriteAtomicallyAsync(iniPath, Encoding.UTF8.GetBytes(mirror.Contents));
        tempIniInstalled = true;

        var mirroredConfig = PalworldRestAcceptanceConfigurationReader.Read(serverRoot);
        if (!mirroredConfig.ConfigExists ||
            !mirroredConfig.RestEnabled ||
            mirroredConfig.RestPort != configuration.RestPort ||
            !mirroredConfig.AdminPasswordConfigured ||
            !string.Equals(mirroredConfig.SelectedWorldId, configuration.SelectedWorldId, StringComparison.OrdinalIgnoreCase))
        {
            Console.Error.WriteLine("Generated PalWorldSettings.ini did not pass Steward's own configuration preflight.");
            return false;
        }

        var loadedPassword = PalworldRestAcceptanceConfigurationReader.ReadAdminPassword(serverRoot);
        if (!string.Equals(loadedPassword, transientPassword, StringComparison.Ordinal))
        {
            loadedPassword = string.Empty;
            Console.Error.WriteLine("Generated PalWorldSettings.ini did not round-trip the transient AdminPassword.");
            return false;
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
            Console.Error.WriteLine("PalServer.exe could not be started.");
            return false;
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
            Console.Error.WriteLine("  blockingReason: authenticated REST /info did not become ready from the mirrored INI.");
            return false;
        }

        worldGuidMatched = string.Equals(
            readiness.Info!.WorldGuid,
            configuration.SelectedWorldId,
            StringComparison.OrdinalIgnoreCase);
        Console.WriteLine($"  worldGuidMatched: {worldGuidMatched}");
        if (!worldGuidMatched)
        {
            Console.Error.WriteLine("  blockingReason: REST reported a different active World.");
            return false;
        }

        using var restSettings = await restClient.GetSettingsAsync();
        var comparison = CompareSettings(
            snapshot,
            restSettings.RootElement,
            configuration.RestPort.Value,
            mirror.ManagementOverrides);
        restSettingsReadable = comparison.RestPropertyCount > 0;
        restExposedSettingsMatch = comparison.Mismatches.Count == 0 && comparison.VerifiedCount > 0;
        verifiedSettingCount = comparison.VerifiedCount;
        unexposedSettingNames = comparison.UnexposedWorldSettings.ToArray();
        restOnlySettingNames = comparison.RestOnlySettings.ToArray();
        mismatchNames = comparison.Mismatches.ToArray();

        Console.WriteLine();
        Console.WriteLine("settingsComparison:");
        Console.WriteLine($"  restPropertyCount: {comparison.RestPropertyCount}");
        Console.WriteLine($"  verifiedWorldSettingCount: {comparison.VerifiedCount}");
        Console.WriteLine($"  mismatchCount: {comparison.Mismatches.Count}");
        Console.WriteLine($"  unexposedWorldSettingCount: {comparison.UnexposedWorldSettings.Count}");
        Console.WriteLine($"  restOnlySettingCount: {comparison.RestOnlySettings.Count}");
        if (comparison.Mismatches.Count > 0)
        {
            Console.WriteLine($"  mismatches: {string.Join(", ", comparison.Mismatches)}");
        }
        if (comparison.UnexposedWorldSettings.Count > 0)
        {
            Console.WriteLine($"  unexposedWorldSettings: {string.Join(", ", comparison.UnexposedWorldSettings)}");
        }
        if (comparison.RestOnlySettings.Count > 0)
        {
            Console.WriteLine($"  restOnlySettings: {string.Join(", ", comparison.RestOnlySettings)}");
        }
        Console.WriteLine($"  restExposedSettingsMatch: {restExposedSettingsMatch}");
        if (!restSettingsReadable || !restExposedSettingsMatch)
        {
            Console.Error.WriteLine("  blockingReason: Palworld's effective REST-exposed settings do not match the WorldOption mirror.");
            return false;
        }

        if (File.Exists(worldOptionPath))
        {
            Console.Error.WriteLine("PalServer generated WorldOption.sav while the canonical file was parked; refusing to overwrite it.");
            return false;
        }

        await WriteAtomicallyAsync(worldOptionPath, originalWorldOptionBytes);
        liveWorldOptionCopyRestored = true;
        await WriteAtomicallyAsync(iniPath, originalIniBytes);
        originalIniRestoredLive = true;

        if (!string.Equals(await Sha256FileAsync(worldOptionPath), originalWorldOptionHash, StringComparison.Ordinal) ||
            !string.Equals(await Sha256FileAsync(iniPath), originalIniHash, StringComparison.Ordinal))
        {
            Console.Error.WriteLine("Byte-exact live restoration of the canonical inputs failed.");
            return false;
        }

        restSettingsStableAfterRestore = true;
        foreach (var checkpoint in new[]
                 {
                     (Name: "immediate", Delay: TimeSpan.Zero),
                     (Name: "+3s", Delay: TimeSpan.FromSeconds(3)),
                     (Name: "+10s", Delay: TimeSpan.FromSeconds(7))
                 })
        {
            if (checkpoint.Delay > TimeSpan.Zero)
            {
                await Task.Delay(checkpoint.Delay);
            }

            try
            {
                var info = await restClient.GetInfoAsync();
                using var settings = await restClient.GetSettingsAsync();
                var current = CompareSettings(
                    snapshot,
                    settings.RootElement,
                    configuration.RestPort.Value,
                    mirror.ManagementOverrides);
                var stable = string.Equals(info.WorldGuid, configuration.SelectedWorldId, StringComparison.OrdinalIgnoreCase) &&
                    current.Mismatches.Count == 0 &&
                    current.VerifiedCount == comparison.VerifiedCount;
                restSettingsStableAfterRestore &= stable;
                Console.WriteLine($"  {checkpoint.Name}.settingsStableAfterOriginalsRestored: {stable}");
            }
            catch (Exception exception) when (
                exception is HttpRequestException or
                InvalidOperationException or
                InvalidDataException or
                TaskCanceledException)
            {
                restSettingsStableAfterRestore = false;
                Console.WriteLine($"  {checkpoint.Name}.failure: {exception.GetType().Name}");
            }
        }

        await restClient.ShutdownAsync(1, "Steward INI-mirror acceptance shutdown.");
        shutdownSucceeded = true;
        allProcessesExited = await WaitForPalworldExitAsync(launcher, TimeSpan.FromSeconds(60));
        Console.WriteLine();
        Console.WriteLine("shutdown:");
        Console.WriteLine($"  requestSucceeded: {shutdownSucceeded}");
        Console.WriteLine($"  allPalworldProcessesExited: {allProcessesExited}");

        if (File.Exists(worldOptionPath))
        {
            liveWorldOptionMutated = !string.Equals(
                await Sha256FileAsync(worldOptionPath),
                originalWorldOptionHash,
                StringComparison.Ordinal);
        }
        if (File.Exists(iniPath))
        {
            liveIniMutated = !string.Equals(
                await Sha256FileAsync(iniPath),
                originalIniHash,
                StringComparison.Ordinal);
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
        EntryPointNotFoundException)
    {
        Console.Error.WriteLine($"INI-mirror acceptance failed: {exception.Message}");
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
                // Process exited while the state was observed.
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
        restExposedSettingsMatch &&
        liveWorldOptionCopyRestored &&
        originalIniRestoredLive &&
        restSettingsStableAfterRestore &&
        shutdownSucceeded &&
        allProcessesExited &&
        !forcedCleanupUsed &&
        !liveWorldOptionMutated &&
        !liveIniMutated &&
        finalWorldOptionHashMatches &&
        finalIniHashMatches &&
        transientCredentialAbsentFromRestoredFiles;

    Console.WriteLine();
    Console.WriteLine("result:");
    Console.WriteLine($"  readinessSucceeded: {readinessSucceeded}");
    Console.WriteLine($"  worldGuidMatched: {worldGuidMatched}");
    Console.WriteLine($"  verifiedWorldSettingCount: {verifiedSettingCount}");
    Console.WriteLine($"  mismatchCount: {mismatchNames.Length}");
    Console.WriteLine($"  unexposedWorldSettingCount: {unexposedSettingNames.Length}");
    Console.WriteLine($"  restOnlySettingCount: {restOnlySettingNames.Length}");
    Console.WriteLine($"  restExposedSettingsMatch: {restExposedSettingsMatch}");
    Console.WriteLine($"  originalsRestoredWhileRunning: {liveWorldOptionCopyRestored && originalIniRestoredLive}");
    Console.WriteLine($"  settingsStableAfterOriginalsRestored: {restSettingsStableAfterRestore}");
    Console.WriteLine($"  shutdownSucceeded: {shutdownSucceeded}");
    Console.WriteLine($"  allPalworldProcessesExited: {allProcessesExited}");
    Console.WriteLine($"  liveWorldOptionMutatedByPalworld: {liveWorldOptionMutated}");
    Console.WriteLine($"  livePalWorldSettingsIniMutatedByPalworld: {liveIniMutated}");
    Console.WriteLine($"  forcedCleanupUsed: {forcedCleanupUsed}");
    Console.WriteLine($"  finalWorldOptionSha256Matches: {finalWorldOptionHashMatches}");
    Console.WriteLine($"  finalPalWorldSettingsIniSha256Matches: {finalIniHashMatches}");
    Console.WriteLine($"  transientCredentialAbsentFromRestoredFiles: {transientCredentialAbsentFromRestoredFiles}");
    Console.WriteLine($"runtimeIniMirrorRestExposedSettingsProven: {proven}");

    return proven;
}

static SettingsComparison CompareSettings(
    PalworldWorldOptionSettingsSnapshot snapshot,
    JsonElement restRoot,
    int restPort,
    IReadOnlySet<string> managementOverrides)
{
    if (restRoot.ValueKind != JsonValueKind.Object)
    {
        throw new InvalidDataException("Palworld /settings did not return a JSON object.");
    }

    var worldSettings = snapshot.Settings.ToDictionary(setting => setting.Name, StringComparer.Ordinal);
    var restProperties = restRoot.EnumerateObject().ToArray();
    var restNames = restProperties.Select(property => property.Name).ToHashSet(StringComparer.Ordinal);
    var mismatches = new List<string>();
    var unexposed = new List<string>();
    var verified = 0;

    foreach (var setting in snapshot.Settings)
    {
        if (setting.IsSensitive || managementOverrides.Contains(setting.Name))
        {
            continue;
        }

        if (!restRoot.TryGetProperty(setting.Name, out var actual))
        {
            unexposed.Add(setting.Name);
            continue;
        }

        if (!RestValueMatches(setting, actual))
        {
            mismatches.Add(setting.Name);
        }
        else
        {
            verified++;
        }
    }

    if (restRoot.TryGetProperty("RESTAPIEnabled", out var restEnabled) &&
        (restEnabled.ValueKind is not JsonValueKind.True))
    {
        mismatches.Add("RESTAPIEnabled(management override)");
    }

    if (restRoot.TryGetProperty("RESTAPIPort", out var restPortElement) &&
        (!TryGetJsonInteger(restPortElement, out var actualRestPort) || actualRestPort != restPort))
    {
        mismatches.Add("RESTAPIPort(management override)");
    }

    var restOnly = restNames
        .Where(name => !worldSettings.ContainsKey(name))
        .OrderBy(name => name, StringComparer.Ordinal)
        .ToArray();

    return new SettingsComparison(
        restProperties.Length,
        verified,
        mismatches.Distinct(StringComparer.Ordinal).OrderBy(name => name, StringComparer.Ordinal).ToArray(),
        unexposed.OrderBy(name => name, StringComparer.Ordinal).ToArray(),
        restOnly);
}

static bool RestValueMatches(PalworldWorldOptionSetting setting, JsonElement actual)
{
    if (setting.PropertyType == "ArrayProperty")
    {
        return RestArrayMatches(setting, actual);
    }

    var expected = PalworldWorldOptionIniMirror.NormalizeExpectedRestValue(setting);
    if (setting.PropertyType == "BoolProperty")
    {
        return bool.TryParse(expected, out var expectedBool) &&
            actual.ValueKind is JsonValueKind.True or JsonValueKind.False &&
            actual.GetBoolean() == expectedBool;
    }

    if (setting.PropertyType is "IntProperty" or "Int64Property" or "UInt32Property" or "UInt64Property")
    {
        return long.TryParse(expected, NumberStyles.Integer, CultureInfo.InvariantCulture, out var expectedSigned)
            ? TryGetJsonInteger(actual, out var actualInteger) && actualInteger == expectedSigned
            : ulong.TryParse(expected, NumberStyles.Integer, CultureInfo.InvariantCulture, out var expectedUnsigned) &&
              actual.ValueKind == JsonValueKind.Number &&
              actual.TryGetUInt64(out var actualUnsigned) &&
              actualUnsigned == expectedUnsigned;
    }

    if (setting.PropertyType is "FloatProperty" or "DoubleProperty")
    {
        if (!double.TryParse(expected, NumberStyles.Float, CultureInfo.InvariantCulture, out var expectedNumber) ||
            actual.ValueKind != JsonValueKind.Number ||
            !actual.TryGetDouble(out var actualNumber))
        {
            return false;
        }

        var tolerance = 1e-6 * Math.Max(1d, Math.Abs(expectedNumber));
        return Math.Abs(actualNumber - expectedNumber) <= tolerance;
    }

    if (setting.PropertyType is "EnumProperty" or "ByteProperty")
    {
        if (actual.ValueKind == JsonValueKind.String)
        {
            return string.Equals(actual.GetString(), expected, StringComparison.Ordinal);
        }

        if (actual.ValueKind == JsonValueKind.Number && long.TryParse(expected, NumberStyles.Integer, CultureInfo.InvariantCulture, out var expectedInteger))
        {
            return TryGetJsonInteger(actual, out var actualInteger) && actualInteger == expectedInteger;
        }

        return false;
    }

    return actual.ValueKind == JsonValueKind.String &&
        string.Equals(actual.GetString(), expected, StringComparison.Ordinal);
}

static bool RestArrayMatches(PalworldWorldOptionSetting setting, JsonElement actual)
{
    if (actual.ValueKind != JsonValueKind.Array || setting.Value is null)
    {
        return false;
    }

    if (!TryNormalizeWorldArray(setting.Value, out var expected))
    {
        return false;
    }

    var actualValues = actual.EnumerateArray().ToArray();
    if (actualValues.Length != expected.Length)
    {
        return false;
    }

    for (var index = 0; index < expected.Length; index++)
    {
        if (actualValues[index].ValueKind != JsonValueKind.String ||
            !string.Equals(actualValues[index].GetString(), expected[index], StringComparison.Ordinal))
        {
            return false;
        }
    }

    return true;
}

static bool TryNormalizeWorldArray(string encoded, out string[] normalized)
{
    if (encoded.Length < 2 || encoded[0] != '(' || encoded[^1] != ')')
    {
        normalized = Array.Empty<string>();
        return false;
    }

    var inner = encoded[1..^1];
    if (inner.Length == 0)
    {
        normalized = Array.Empty<string>();
        return true;
    }

    var entries = inner.Split(',', StringSplitOptions.None);
    normalized = new string[entries.Length];
    for (var index = 0; index < entries.Length; index++)
    {
        var entry = entries[index].Trim();
        if (entry.Length == 0)
        {
            normalized = Array.Empty<string>();
            return false;
        }

        var enumSeparator = entry.LastIndexOf("::", StringComparison.Ordinal);
        normalized[index] = enumSeparator >= 0 ? entry[(enumSeparator + 2)..] : entry;
    }

    return true;
}

static bool TryGetJsonInteger(JsonElement value, out long result)
{
    if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out result))
    {
        return true;
    }

    result = default;
    return false;
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
            var info = await restClient.GetInfoAsync();
            return new ReadinessResult(info, DateTimeOffset.UtcNow - started, "none");
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
                    // Process disappeared or could not be killed; final process observation decides safety.
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

internal sealed record SettingsComparison(
    int RestPropertyCount,
    int VerifiedCount,
    IReadOnlyList<string> Mismatches,
    IReadOnlyList<string> UnexposedWorldSettings,
    IReadOnlyList<string> RestOnlySettings);
