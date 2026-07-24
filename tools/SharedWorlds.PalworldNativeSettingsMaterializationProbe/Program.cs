using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using SharedWorlds.GameAdapters.Palworld;

const string LauncherProcessName = "PalServer";
const string ShippingProcessName = "PalServer-Win64-Shipping-Cmd";
const uint CreateNewProcessGroup = 0x00000200;
const uint CtrlBreakEvent = 1;

Console.WriteLine("SharedWorlds Palworld-native settings materialization acceptance");
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

    var configDirectory = Path.Combine(serverRoot, "Pal", "Saved", "Config", "WindowsServer");
    var gameUserSettingsPath = Path.Combine(configDirectory, "GameUserSettings.ini");
    var palWorldSettingsPath = Path.Combine(configDirectory, "PalWorldSettings.ini");
    if (!File.Exists(gameUserSettingsPath) || !File.Exists(palWorldSettingsPath))
    {
        Console.Error.WriteLine(
            "GameUserSettings.ini and PalWorldSettings.ini must both exist before this acceptance probe.");
        return false;
    }

    var currentConfiguration = PalworldRestAcceptanceConfigurationReader.Read(serverRoot);
    if (string.IsNullOrWhiteSpace(currentConfiguration.SelectedWorldId))
    {
        Console.Error.WriteLine("GameUserSettings.ini does not select a dedicated World.");
        return false;
    }

    var selectedWorldId = currentConfiguration.SelectedWorldId;
    var savesRoot = Path.Combine(serverRoot, "Pal", "Saved", "SaveGames", "0");
    var selectedWorldPath = Path.Combine(savesRoot, selectedWorldId);
    var selectedWorldOptionPath = Path.Combine(selectedWorldPath, "WorldOption.sav");
    if (!File.Exists(Path.Combine(selectedWorldPath, "Level.sav")) || !File.Exists(selectedWorldOptionPath))
    {
        Console.Error.WriteLine(
            "The selected dedicated World must contain Level.sav and WorldOption.sav for this experiment.");
        return false;
    }

    var originalGameUserSettingsBytes = await File.ReadAllBytesAsync(gameUserSettingsPath);
    var originalPalWorldSettingsBytes = await File.ReadAllBytesAsync(palWorldSettingsPath);
    var originalWorldOptionBytes = await File.ReadAllBytesAsync(selectedWorldOptionPath);
    var originalGameUserSettingsHash = Sha256(originalGameUserSettingsBytes);
    var originalPalWorldSettingsHash = Sha256(originalPalWorldSettingsBytes);
    var originalWorldOptionHash = Sha256(originalWorldOptionBytes);
    var originalIniWriteTimeUtc = File.GetLastWriteTimeUtc(palWorldSettingsPath);

    var temporaryWorldId = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
    var temporaryWorldPath = Path.Combine(savesRoot, temporaryWorldId);

    Process? launcher = null;
    PalworldOodleCodec? oracleOodle = null;
    var temporaryWorldCreated = false;
    var temporarySelectionInstalled = false;
    var startupSignalObserved = false;
    var ctrlBreakSent = false;
    var ctrlBreakShutdownSucceeded = false;
    var allProcessesExited = false;
    var forcedCleanupUsed = false;
    var rewrittenIniObserved = false;
    var rewrittenIniTimestampAdvanced = false;
    var rewrittenIniParsed = false;
    var verifiedWorldSettingCount = 0;
    var mismatchNames = Array.Empty<string>();
    var unrepresentedWorldSettings = Array.Empty<string>();
    var unexpectedIniSettings = Array.Empty<string>();
    var originalConfigsRestored = false;
    var temporaryWorldDeleted = false;
    var selectedWorldOptionUnchanged = false;
    var finalGameUserSettingsHashMatches = false;
    var finalPalWorldSettingsHashMatches = false;

    try
    {
        if (PalworldWorldOptionAdminPasswordRuntimeOverlay.RequiresOodle(originalWorldOptionBytes))
        {
            oracleOodle = PalworldOodleCodec.LoadFromPalworldRoots(serverRoot, installation.RootPath);
            Console.WriteLine($"oracleOodleLibrary: {oracleOodle.LibraryPath}");
            Console.WriteLine("oracleOodleUsage: decode-only; acceptance verdict only");
        }

        var snapshot = PalworldWorldOptionSettingsReader.Read(originalWorldOptionBytes, oracleOodle);
        var expectedMirror = PalworldWorldOptionIniMirror.Create(snapshot, "ORACLE_ONLY_NOT_WRITTEN", 8212);
        var managementOverrides = expectedMirror.ManagementOverrides;

        Console.WriteLine("preflight:");
        Console.WriteLine($"  selectedWorldId: {selectedWorldId}");
        Console.WriteLine($"  temporaryWorldId: {temporaryWorldId}");
        Console.WriteLine($"  container: {snapshot.Container}");
        Console.WriteLine($"  extractedSettings: {snapshot.Settings.Count}");
        Console.WriteLine($"  oracleComparableSettings: {snapshot.Settings.Count(setting => !managementOverrides.Contains(setting.Name))}");
        Console.WriteLine($"  selectedWorldOptionSha256: {originalWorldOptionHash}");
        Console.WriteLine($"  originalPalWorldSettingsSha256: {originalPalWorldSettingsHash}");
        Console.WriteLine();

        CopyDirectory(selectedWorldPath, temporaryWorldPath);
        temporaryWorldCreated = true;

        var temporaryWorldOptionPath = Path.Combine(temporaryWorldPath, "WorldOption.sav");
        if (!File.Exists(temporaryWorldOptionPath) ||
            !string.Equals(await Sha256FileAsync(temporaryWorldOptionPath), originalWorldOptionHash, StringComparison.Ordinal))
        {
            throw new InvalidDataException("The disposable World clone did not preserve WorldOption.sav byte-for-byte.");
        }

        var gameUserSettingsText = Encoding.UTF8.GetString(originalGameUserSettingsBytes);
        var selectedForProbe = ReplaceDedicatedServerName(gameUserSettingsText, temporaryWorldId);
        if (string.Equals(gameUserSettingsText, selectedForProbe, StringComparison.Ordinal))
        {
            throw new InvalidDataException("GameUserSettings.ini could not be redirected to the disposable World.");
        }

        await WriteAtomicallyAsync(gameUserSettingsPath, Encoding.UTF8.GetBytes(selectedForProbe));
        temporarySelectionInstalled = true;

        var selectedAfterWrite = PalworldRestAcceptanceConfigurationReader.Read(serverRoot).SelectedWorldId;
        if (!string.Equals(selectedAfterWrite, temporaryWorldId, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("PalServer configuration did not select the disposable World after redirection.");
        }

        launcher = StartInNewProcessGroup(serverExecutable, serverRoot);
        Console.WriteLine("translatorBoot:");
        Console.WriteLine($"  launcherProcessId: {launcher.Id}");

        startupSignalObserved = await WaitForShippingProcessAsync(launcher, TimeSpan.FromSeconds(90));
        Console.WriteLine($"  shippingProcessObserved: {startupSignalObserved}");
        if (!startupSignalObserved)
        {
            throw new InvalidOperationException(
                "PalServer-Win64-Shipping-Cmd did not appear for the disposable translator boot.");
        }

        // REST is intentionally not used here: this experiment asks whether Palworld itself can
        // materialize WorldOption settings without Steward understanding PlM. Give the Shipping
        // process time to finish normal World/config startup before requesting console shutdown.
        await Task.Delay(TimeSpan.FromSeconds(20));
        if (!IsAnyPalworldProcessRunning())
        {
            throw new InvalidOperationException("Palworld exited before the translator shutdown signal was sent.");
        }

        if (!NativeMethods.GenerateConsoleCtrlEvent(CtrlBreakEvent, (uint)launcher.Id))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "GenerateConsoleCtrlEvent(CTRL_BREAK_EVENT) failed.");
        }

        ctrlBreakSent = true;
        Console.WriteLine($"  ctrlBreakSent: {ctrlBreakSent}");

        allProcessesExited = await WaitForPalworldExitAsync(launcher, TimeSpan.FromSeconds(60));
        ctrlBreakShutdownSucceeded = allProcessesExited;
        Console.WriteLine($"  ctrlBreakShutdownSucceeded: {ctrlBreakShutdownSucceeded}");
        Console.WriteLine($"  allPalworldProcessesExited: {allProcessesExited}");
        if (!allProcessesExited)
        {
            throw new InvalidOperationException(
                "Palworld did not fully exit after the disposable translator CTRL_BREAK request.");
        }

        if (!File.Exists(palWorldSettingsPath))
        {
            throw new InvalidDataException("Palworld removed PalWorldSettings.ini during the translator boot.");
        }

        var rewrittenIniBytes = await File.ReadAllBytesAsync(palWorldSettingsPath);
        rewrittenIniObserved = !rewrittenIniBytes.AsSpan().SequenceEqual(originalPalWorldSettingsBytes);
        rewrittenIniTimestampAdvanced = File.GetLastWriteTimeUtc(palWorldSettingsPath) > originalIniWriteTimeUtc;

        var actualSettings = ParseOptionSettings(Encoding.UTF8.GetString(rewrittenIniBytes));
        rewrittenIniParsed = actualSettings.Count > 0;
        if (!rewrittenIniParsed)
        {
            throw new InvalidDataException("Palworld's post-shutdown PalWorldSettings.ini did not contain parseable OptionSettings.");
        }

        var mismatches = new List<string>();
        var unrepresented = new List<string>();
        var verified = 0;

        foreach (var setting in snapshot.Settings)
        {
            if (managementOverrides.Contains(setting.Name))
            {
                continue;
            }

            if (!expectedMirror.SerializedValues.TryGetValue(setting.Name, out var expectedValue))
            {
                throw new InvalidDataException(
                    $"The acceptance oracle did not serialize WorldOption setting {setting.Name}.");
            }

            if (!actualSettings.TryGetValue(setting.Name, out var actualValue))
            {
                unrepresented.Add(setting.Name);
                continue;
            }

            if (!IniValuesSemanticallyEqual(setting, expectedValue, actualValue))
            {
                mismatches.Add(setting.Name);
                continue;
            }

            verified++;
        }

        verifiedWorldSettingCount = verified;
        mismatchNames = mismatches.Order(StringComparer.Ordinal).ToArray();
        unrepresentedWorldSettings = unrepresented.Order(StringComparer.Ordinal).ToArray();
        unexpectedIniSettings = actualSettings.Keys
            .Where(name =>
                !managementOverrides.Contains(name) &&
                !snapshot.Settings.Any(setting => string.Equals(setting.Name, name, StringComparison.Ordinal)))
            .Order(StringComparer.Ordinal)
            .ToArray();

        Console.WriteLine();
        Console.WriteLine("materializedIni:");
        Console.WriteLine($"  bytesChanged: {rewrittenIniObserved}");
        Console.WriteLine($"  timestampAdvanced: {rewrittenIniTimestampAdvanced}");
        Console.WriteLine($"  parsedSettingCount: {actualSettings.Count}");
        Console.WriteLine($"  verifiedWorldSettingCount: {verifiedWorldSettingCount}");
        Console.WriteLine($"  mismatchCount: {mismatchNames.Length}");
        Console.WriteLine($"  unrepresentedWorldSettingCount: {unrepresentedWorldSettings.Length}");
        Console.WriteLine($"  unexpectedIniSettingCount: {unexpectedIniSettings.Length}");
        if (mismatchNames.Length > 0)
        {
            Console.WriteLine($"  mismatchNames: {string.Join(", ", mismatchNames)}");
        }
        if (unrepresentedWorldSettings.Length > 0)
        {
            Console.WriteLine($"  unrepresentedWorldSettings: {string.Join(", ", unrepresentedWorldSettings)}");
        }
        if (unexpectedIniSettings.Length > 0)
        {
            Console.WriteLine($"  unexpectedIniSettings: {string.Join(", ", unexpectedIniSettings)}");
        }

        await RestoreExactConfigsAsync(
            gameUserSettingsPath,
            originalGameUserSettingsBytes,
            palWorldSettingsPath,
            originalPalWorldSettingsBytes);
        temporarySelectionInstalled = false;
        originalConfigsRestored = true;

        Directory.Delete(temporaryWorldPath, recursive: true);
        temporaryWorldCreated = false;
        temporaryWorldDeleted = true;

        selectedWorldOptionUnchanged =
            File.Exists(selectedWorldOptionPath) &&
            string.Equals(await Sha256FileAsync(selectedWorldOptionPath), originalWorldOptionHash, StringComparison.Ordinal);
        finalGameUserSettingsHashMatches = string.Equals(
            await Sha256FileAsync(gameUserSettingsPath),
            originalGameUserSettingsHash,
            StringComparison.Ordinal);
        finalPalWorldSettingsHashMatches = string.Equals(
            await Sha256FileAsync(palWorldSettingsPath),
            originalPalWorldSettingsHash,
            StringComparison.Ordinal);
    }
    catch (Exception exception) when (
        exception is IOException or
        UnauthorizedAccessException or
        InvalidOperationException or
        InvalidDataException or
        TaskCanceledException or
        DllNotFoundException or
        BadImageFormatException or
        EntryPointNotFoundException or
        ArgumentException or
        OverflowException or
        Win32Exception)
    {
        Console.Error.WriteLine($"Native settings materialization acceptance failed: {exception.Message}");
    }
    finally
    {
        oracleOodle?.Dispose();

        if (IsAnyPalworldProcessRunning())
        {
            forcedCleanupUsed = await ForceCleanupPalworldProcessesAsync();
        }

        if (!IsAnyPalworldProcessRunning())
        {
            try
            {
                if (temporarySelectionInstalled ||
                    !File.Exists(gameUserSettingsPath) ||
                    !File.Exists(palWorldSettingsPath))
                {
                    await RestoreExactConfigsAsync(
                        gameUserSettingsPath,
                        originalGameUserSettingsBytes,
                        palWorldSettingsPath,
                        originalPalWorldSettingsBytes);
                    temporarySelectionInstalled = false;
                }
                else
                {
                    // Even after a successful explicit restoration, enforce byte-exact originals.
                    if (!string.Equals(
                            await Sha256FileAsync(gameUserSettingsPath),
                            originalGameUserSettingsHash,
                            StringComparison.Ordinal) ||
                        !string.Equals(
                            await Sha256FileAsync(palWorldSettingsPath),
                            originalPalWorldSettingsHash,
                            StringComparison.Ordinal))
                    {
                        await RestoreExactConfigsAsync(
                            gameUserSettingsPath,
                            originalGameUserSettingsBytes,
                            palWorldSettingsPath,
                            originalPalWorldSettingsBytes);
                    }
                }

                originalConfigsRestored =
                    File.Exists(gameUserSettingsPath) &&
                    File.Exists(palWorldSettingsPath) &&
                    string.Equals(
                        await Sha256FileAsync(gameUserSettingsPath),
                        originalGameUserSettingsHash,
                        StringComparison.Ordinal) &&
                    string.Equals(
                        await Sha256FileAsync(palWorldSettingsPath),
                        originalPalWorldSettingsHash,
                        StringComparison.Ordinal);

                if (temporaryWorldCreated && Directory.Exists(temporaryWorldPath))
                {
                    Directory.Delete(temporaryWorldPath, recursive: true);
                    temporaryWorldCreated = false;
                }

                temporaryWorldDeleted = !Directory.Exists(temporaryWorldPath);
                selectedWorldOptionUnchanged =
                    File.Exists(selectedWorldOptionPath) &&
                    string.Equals(
                        await Sha256FileAsync(selectedWorldOptionPath),
                        originalWorldOptionHash,
                        StringComparison.Ordinal);
                finalGameUserSettingsHashMatches =
                    File.Exists(gameUserSettingsPath) &&
                    string.Equals(
                        await Sha256FileAsync(gameUserSettingsPath),
                        originalGameUserSettingsHash,
                        StringComparison.Ordinal);
                finalPalWorldSettingsHashMatches =
                    File.Exists(palWorldSettingsPath) &&
                    string.Equals(
                        await Sha256FileAsync(palWorldSettingsPath),
                        originalPalWorldSettingsHash,
                        StringComparison.Ordinal);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                Console.Error.WriteLine($"CRITICAL: translator cleanup failed: {exception.Message}");
            }
        }
        else
        {
            Console.Error.WriteLine(
                "CRITICAL: a Palworld process is still running. Steward deliberately did not restore shared server config or delete the disposable World underneath the live process.");
        }

        launcher?.Dispose();
        CryptographicOperations.ZeroMemory(originalGameUserSettingsBytes);
        CryptographicOperations.ZeroMemory(originalPalWorldSettingsBytes);
        CryptographicOperations.ZeroMemory(originalWorldOptionBytes);
    }

    var nativeMaterializationMatches =
        rewrittenIniParsed &&
        verifiedWorldSettingCount > 0 &&
        mismatchNames.Length == 0 &&
        unrepresentedWorldSettings.Length == 0;

    var proven =
        startupSignalObserved &&
        ctrlBreakSent &&
        ctrlBreakShutdownSucceeded &&
        allProcessesExited &&
        nativeMaterializationMatches &&
        !forcedCleanupUsed &&
        originalConfigsRestored &&
        temporaryWorldDeleted &&
        selectedWorldOptionUnchanged &&
        finalGameUserSettingsHashMatches &&
        finalPalWorldSettingsHashMatches;

    Console.WriteLine();
    Console.WriteLine("result:");
    Console.WriteLine($"  startupSignalObserved: {startupSignalObserved}");
    Console.WriteLine($"  ctrlBreakSent: {ctrlBreakSent}");
    Console.WriteLine($"  ctrlBreakShutdownSucceeded: {ctrlBreakShutdownSucceeded}");
    Console.WriteLine($"  allPalworldProcessesExited: {allProcessesExited}");
    Console.WriteLine($"  rewrittenIniObserved: {rewrittenIniObserved}");
    Console.WriteLine($"  rewrittenIniTimestampAdvanced: {rewrittenIniTimestampAdvanced}");
    Console.WriteLine($"  rewrittenIniParsed: {rewrittenIniParsed}");
    Console.WriteLine($"  verifiedWorldSettingCount: {verifiedWorldSettingCount}");
    Console.WriteLine($"  mismatchCount: {mismatchNames.Length}");
    Console.WriteLine($"  unrepresentedWorldSettingCount: {unrepresentedWorldSettings.Length}");
    Console.WriteLine($"  originalConfigsRestored: {originalConfigsRestored}");
    Console.WriteLine($"  temporaryWorldDeleted: {temporaryWorldDeleted}");
    Console.WriteLine($"  selectedWorldOptionUnchanged: {selectedWorldOptionUnchanged}");
    Console.WriteLine($"  forcedCleanupUsed: {forcedCleanupUsed}");
    Console.WriteLine($"  finalGameUserSettingsSha256Matches: {finalGameUserSettingsHashMatches}");
    Console.WriteLine($"  finalPalWorldSettingsSha256Matches: {finalPalWorldSettingsHashMatches}");
    Console.WriteLine($"palworldNativeSettingsMaterializationProven: {proven}");

    return proven;
}

static Process StartInNewProcessGroup(string executablePath, string workingDirectory)
{
    var startupInfo = new NativeMethods.StartupInfo
    {
        cb = Marshal.SizeOf<NativeMethods.StartupInfo>()
    };

    if (!NativeMethods.CreateProcess(
            executablePath,
            null,
            IntPtr.Zero,
            IntPtr.Zero,
            inheritHandles: false,
            CreateNewProcessGroup,
            IntPtr.Zero,
            workingDirectory,
            ref startupInfo,
            out var processInformation))
    {
        throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateProcess failed for PalServer.exe.");
    }

    try
    {
        return Process.GetProcessById((int)processInformation.dwProcessId);
    }
    finally
    {
        NativeMethods.CloseHandle(processInformation.hThread);
        NativeMethods.CloseHandle(processInformation.hProcess);
    }
}

static async Task<bool> WaitForShippingProcessAsync(Process launcher, TimeSpan timeout)
{
    var deadline = DateTimeOffset.UtcNow + timeout;
    while (DateTimeOffset.UtcNow < deadline)
    {
        if (launcher.HasExited && !IsAnyPalworldProcessRunning())
        {
            return false;
        }

        if (IsProcessRunning(ShippingProcessName))
        {
            return true;
        }

        await Task.Delay(250);
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
                    exception is InvalidOperationException or Win32Exception)
                {
                    // Final process observation decides whether config restoration is safe.
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

static string ReplaceDedicatedServerName(string text, string worldId)
{
    var expression = new Regex(
        @"^(?<prefix>\s*DedicatedServerName\s*=\s*).*$",
        RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.CultureInvariant);
    var match = expression.Match(text);
    if (!match.Success)
    {
        throw new InvalidDataException("DedicatedServerName was not found in GameUserSettings.ini.");
    }

    return expression.Replace(
        text,
        current => current.Groups["prefix"].Value + worldId,
        count: 1);
}

static void CopyDirectory(string sourcePath, string destinationPath)
{
    if (Directory.Exists(destinationPath))
    {
        throw new IOException($"Disposable World path already exists: {destinationPath}");
    }

    Directory.CreateDirectory(destinationPath);
    foreach (var directory in Directory.EnumerateDirectories(sourcePath, "*", SearchOption.AllDirectories))
    {
        var attributes = File.GetAttributes(directory);
        if ((attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException($"Refusing to clone reparse-point directory: {directory}");
        }

        var relative = Path.GetRelativePath(sourcePath, directory);
        Directory.CreateDirectory(Path.Combine(destinationPath, relative));
    }

    foreach (var file in Directory.EnumerateFiles(sourcePath, "*", SearchOption.AllDirectories))
    {
        var attributes = File.GetAttributes(file);
        if ((attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException($"Refusing to clone reparse-point file: {file}");
        }

        var relative = Path.GetRelativePath(sourcePath, file);
        var destination = Path.Combine(destinationPath, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        File.Copy(file, destination, overwrite: false);
    }
}

static async Task RestoreExactConfigsAsync(
    string gameUserSettingsPath,
    byte[] gameUserSettingsBytes,
    string palWorldSettingsPath,
    byte[] palWorldSettingsBytes)
{
    await WriteAtomicallyAsync(gameUserSettingsPath, gameUserSettingsBytes);
    await WriteAtomicallyAsync(palWorldSettingsPath, palWorldSettingsBytes);
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

static IReadOnlyDictionary<string, string> ParseOptionSettings(string iniText)
{
    var optionIndex = iniText.IndexOf("OptionSettings", StringComparison.OrdinalIgnoreCase);
    if (optionIndex < 0)
    {
        throw new InvalidDataException("OptionSettings was not found in PalWorldSettings.ini.");
    }

    var equalsIndex = iniText.IndexOf('=', optionIndex + "OptionSettings".Length);
    if (equalsIndex < 0)
    {
        throw new InvalidDataException("OptionSettings does not contain '='.");
    }

    var openIndex = iniText.IndexOf('(', equalsIndex + 1);
    if (openIndex < 0)
    {
        throw new InvalidDataException("OptionSettings does not contain an opening tuple parenthesis.");
    }

    var closeIndex = FindMatchingParenthesis(iniText, openIndex);
    var inner = iniText[(openIndex + 1)..closeIndex];
    var result = new Dictionary<string, string>(StringComparer.Ordinal);
    foreach (var item in SplitTopLevel(inner, ','))
    {
        if (string.IsNullOrWhiteSpace(item))
        {
            continue;
        }

        var itemEquals = FindTopLevelCharacter(item, '=');
        if (itemEquals <= 0)
        {
            throw new InvalidDataException($"Malformed OptionSettings entry without key/value separator: {item.Trim()}");
        }

        var key = item[..itemEquals].Trim();
        var value = item[(itemEquals + 1)..].Trim();
        if (string.IsNullOrWhiteSpace(key) || !result.TryAdd(key, value))
        {
            throw new InvalidDataException($"OptionSettings contains an empty or duplicate setting name: {key}");
        }
    }

    return result;
}

static int FindMatchingParenthesis(string text, int openIndex)
{
    var depth = 0;
    var quoted = false;
    var escaped = false;
    for (var index = openIndex; index < text.Length; index++)
    {
        var character = text[index];
        if (quoted)
        {
            if (escaped)
            {
                escaped = false;
            }
            else if (character == '\\')
            {
                escaped = true;
            }
            else if (character == '"')
            {
                quoted = false;
            }
            continue;
        }

        if (character == '"')
        {
            quoted = true;
        }
        else if (character == '(')
        {
            depth++;
        }
        else if (character == ')')
        {
            depth--;
            if (depth == 0)
            {
                return index;
            }
            if (depth < 0)
            {
                break;
            }
        }
    }

    throw new InvalidDataException("OptionSettings contains unbalanced parentheses or quotes.");
}

static IReadOnlyList<string> SplitTopLevel(string text, char separator)
{
    var parts = new List<string>();
    var start = 0;
    var depth = 0;
    var quoted = false;
    var escaped = false;
    for (var index = 0; index < text.Length; index++)
    {
        var character = text[index];
        if (quoted)
        {
            if (escaped)
            {
                escaped = false;
            }
            else if (character == '\\')
            {
                escaped = true;
            }
            else if (character == '"')
            {
                quoted = false;
            }
            continue;
        }

        if (character == '"')
        {
            quoted = true;
        }
        else if (character == '(')
        {
            depth++;
        }
        else if (character == ')')
        {
            depth--;
            if (depth < 0)
            {
                throw new InvalidDataException("OptionSettings contains an unexpected closing parenthesis.");
            }
        }
        else if (character == separator && depth == 0)
        {
            parts.Add(text[start..index]);
            start = index + 1;
        }
    }

    if (quoted || depth != 0)
    {
        throw new InvalidDataException("OptionSettings contains an unterminated quote or nested tuple.");
    }

    parts.Add(text[start..]);
    return parts;
}

static int FindTopLevelCharacter(string text, char target)
{
    var depth = 0;
    var quoted = false;
    var escaped = false;
    for (var index = 0; index < text.Length; index++)
    {
        var character = text[index];
        if (quoted)
        {
            if (escaped)
            {
                escaped = false;
            }
            else if (character == '\\')
            {
                escaped = true;
            }
            else if (character == '"')
            {
                quoted = false;
            }
            continue;
        }

        if (character == '"')
        {
            quoted = true;
        }
        else if (character == '(')
        {
            depth++;
        }
        else if (character == ')')
        {
            depth--;
        }
        else if (character == target && depth == 0)
        {
            return index;
        }
    }

    return -1;
}

static bool IniValuesSemanticallyEqual(
    PalworldWorldOptionSetting setting,
    string expected,
    string actual)
{
    try
    {
        return setting.PropertyType switch
        {
            "BoolProperty" => ParseBoolean(expected) == ParseBoolean(actual),
            "IntProperty" or "Int64Property" or "UInt32Property" or "UInt64Property" =>
                ParseInteger(expected) == ParseInteger(actual),
            "FloatProperty" or "DoubleProperty" => FloatingPointEqual(expected, actual),
            "StrProperty" or "NameProperty" =>
                string.Equals(Unquote(expected), Unquote(actual), StringComparison.Ordinal),
            "EnumProperty" or "ByteProperty" =>
                string.Equals(StripEnumPrefix(Unquote(expected)), StripEnumPrefix(Unquote(actual)), StringComparison.Ordinal),
            "ArrayProperty" => ArraysEqual(expected, actual),
            _ => false
        };
    }
    catch (Exception exception) when (
        exception is FormatException or OverflowException or InvalidDataException)
    {
        return false;
    }
}

static bool ParseBoolean(string value)
    => bool.TryParse(Unquote(value), out var parsed)
        ? parsed
        : throw new FormatException("Invalid Boolean INI value.");

static decimal ParseInteger(string value)
    => decimal.TryParse(
        Unquote(value),
        NumberStyles.Integer,
        CultureInfo.InvariantCulture,
        out var parsed)
        ? parsed
        : throw new FormatException("Invalid integer INI value.");

static bool FloatingPointEqual(string left, string right)
{
    if (!double.TryParse(Unquote(left), NumberStyles.Float, CultureInfo.InvariantCulture, out var leftValue) ||
        !double.TryParse(Unquote(right), NumberStyles.Float, CultureInfo.InvariantCulture, out var rightValue) ||
        double.IsNaN(leftValue) ||
        double.IsNaN(rightValue) ||
        double.IsInfinity(leftValue) ||
        double.IsInfinity(rightValue))
    {
        return false;
    }

    var scale = Math.Max(1.0, Math.Max(Math.Abs(leftValue), Math.Abs(rightValue)));
    return Math.Abs(leftValue - rightValue) <= 1e-6 * scale;
}

static bool ArraysEqual(string expected, string actual)
{
    var expectedItems = ParseTuple(expected);
    var actualItems = ParseTuple(actual);
    if (expectedItems.Count != actualItems.Count)
    {
        return false;
    }

    for (var index = 0; index < expectedItems.Count; index++)
    {
        if (!string.Equals(
                StripEnumPrefix(Unquote(expectedItems[index])),
                StripEnumPrefix(Unquote(actualItems[index])),
                StringComparison.Ordinal))
        {
            return false;
        }
    }

    return true;
}

static IReadOnlyList<string> ParseTuple(string value)
{
    var trimmed = value.Trim();
    if (trimmed.Length == 0 || string.Equals(trimmed, "()", StringComparison.Ordinal))
    {
        return Array.Empty<string>();
    }

    if (trimmed.Length < 2 || trimmed[0] != '(' || trimmed[^1] != ')')
    {
        throw new InvalidDataException("Array INI value is not a tuple.");
    }

    var inner = trimmed[1..^1];
    return inner.Length == 0 ? Array.Empty<string>() : SplitTopLevel(inner, ',');
}

static string Unquote(string value)
{
    var trimmed = value.Trim();
    if (trimmed.Length < 2 || trimmed[0] != '"' || trimmed[^1] != '"')
    {
        return trimmed;
    }

    var builder = new StringBuilder(trimmed.Length - 2);
    var escaped = false;
    for (var index = 1; index < trimmed.Length - 1; index++)
    {
        var character = trimmed[index];
        if (escaped)
        {
            builder.Append(character);
            escaped = false;
        }
        else if (character == '\\')
        {
            escaped = true;
        }
        else
        {
            builder.Append(character);
        }
    }

    if (escaped)
    {
        throw new InvalidDataException("Quoted INI value ends with an incomplete escape.");
    }

    return builder.ToString();
}

static string StripEnumPrefix(string value)
{
    var separator = value.LastIndexOf("::", StringComparison.Ordinal);
    return separator >= 0 ? value[(separator + 2)..] : value;
}

static string Sha256(ReadOnlySpan<byte> bytes)
    => Convert.ToHexString(SHA256.HashData(bytes));

static async Task<string> Sha256FileAsync(string path)
    => Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(path)));

internal static partial class NativeMethods
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct StartupInfo
    {
        internal int cb;
        internal string? lpReserved;
        internal string? lpDesktop;
        internal string? lpTitle;
        internal int dwX;
        internal int dwY;
        internal int dwXSize;
        internal int dwYSize;
        internal int dwXCountChars;
        internal int dwYCountChars;
        internal int dwFillAttribute;
        internal int dwFlags;
        internal short wShowWindow;
        internal short cbReserved2;
        internal IntPtr lpReserved2;
        internal IntPtr hStdInput;
        internal IntPtr hStdOutput;
        internal IntPtr hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct ProcessInformation
    {
        internal IntPtr hProcess;
        internal IntPtr hThread;
        internal uint dwProcessId;
        internal uint dwThreadId;
    }

    [LibraryImport("kernel32.dll", EntryPoint = "CreateProcessW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool CreateProcess(
        string applicationName,
        StringBuilder? commandLine,
        IntPtr processAttributes,
        IntPtr threadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandles,
        uint creationFlags,
        IntPtr environment,
        string currentDirectory,
        ref StartupInfo startupInfo,
        out ProcessInformation processInformation);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GenerateConsoleCtrlEvent(uint ctrlEvent, uint processGroupId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool CloseHandle(IntPtr handle);
}
