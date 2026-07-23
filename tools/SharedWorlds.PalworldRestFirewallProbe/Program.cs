using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SharedWorlds.GameAdapters.Palworld;

const string LauncherProcessName = "PalServer";
const string ShippingProcessName = "PalServer-Win64-Shipping-Cmd";
const string FirewallRulePrefix = "Steward-Palworld-REST-Acceptance-";

Console.WriteLine("SharedWorlds Palworld REST Windows Firewall acceptance");
Console.WriteLine();

if (!OperatingSystem.IsWindows())
{
    Console.Error.WriteLine("This acceptance probe requires Windows and the real Palworld dedicated server.");
    Environment.ExitCode = 2;
    return;
}

Environment.ExitCode = await RunAsync() ? 0 : 2;

static async Task<bool> RunAsync()
{
    var administratorPreflightSucceeded = await IsAdministratorAsync();
    if (!administratorPreflightSucceeded)
    {
        Console.Error.WriteLine(
            "Windows Firewall acceptance requires an elevated PowerShell/terminal. No Palworld or firewall state was changed.");
        return false;
    }

    var staleRules = await FindStaleFirewallRulesAsync();
    if (staleRules.Count > 0)
    {
        Console.Error.WriteLine("A prior Steward Palworld firewall acceptance rule still exists. Refusing to modify it automatically:");
        foreach (var staleRule in staleRules)
        {
            Console.Error.WriteLine($"  {staleRule}");
        }

        Console.Error.WriteLine("Resolve the stale rule deliberately before running another acceptance experiment.");
        return false;
    }

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

    var shippingExecutable = Path.Combine(
        serverRoot,
        "Pal",
        "Binaries",
        "Win64",
        ShippingProcessName + ".exe");
    if (!File.Exists(shippingExecutable))
    {
        Console.Error.WriteLine($"The Palworld Shipping executable was not found at '{shippingExecutable}'.");
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
    var parkingPath = worldOptionPath + $".sharedworlds-firewall-{Guid.NewGuid():N}";
    var transientPassword = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
    var firewallRuleName = FirewallRulePrefix + Guid.NewGuid().ToString("N");

    PalworldOodleCodec? oodleCodec = null;
    Process? launcher = null;
    HttpClient? httpClient = null;
    PalworldRestApiClient? restClient = null;
    var parked = false;
    var tempIniInstalled = false;
    var readinessSucceeded = false;
    var worldGuidMatched = false;
    var firewallRuleInstalled = false;
    var loopbackRestWorksWithFirewall = false;
    var lanPeerBlockOperatorConfirmed = false;
    var saveSucceeded = false;
    var shutdownSucceeded = false;
    var allProcessesExited = false;
    var firewallRuleRemovedAfterProcessExit = false;
    var firewallRuleAbsentFinal = false;
    var forcedCleanupUsed = false;
    var unexpectedWorldOptionGenerated = false;
    var originalsRestoredAfterProcessExit = false;
    var finalWorldOptionHashMatches = false;
    var finalIniHashMatches = false;
    var transientCredentialAbsentFromRestoredFiles = false;

    try
    {
        if (PalworldWorldOptionAdminPasswordRuntimeOverlay.RequiresOodle(originalWorldOptionBytes))
        {
            oodleCodec = PalworldOodleCodec.LoadFromPalworldRoots(serverRoot, installation.RootPath);
            Console.WriteLine($"oodleLibrary: {oodleCodec.LibraryPath}");
            Console.WriteLine("oodleUsage: decode-only acceptance");
        }

        var snapshot = PalworldWorldOptionSettingsReader.Read(originalWorldOptionBytes, oodleCodec);
        var mirror = PalworldWorldOptionIniMirror.Create(
            snapshot,
            transientPassword,
            configuration.RestPort.Value);
        var temporaryIniBytes = Encoding.UTF8.GetBytes(mirror.Contents);

        Console.WriteLine("preflight:");
        Console.WriteLine($"  administrator: {administratorPreflightSucceeded}");
        Console.WriteLine($"  worldId: {configuration.SelectedWorldId}");
        Console.WriteLine($"  restPort: {configuration.RestPort.Value}");
        Console.WriteLine($"  shippingExecutable: {shippingExecutable}");
        Console.WriteLine($"  firewallRuleName: {firewallRuleName}");
        Console.WriteLine($"  extractedSettings: {snapshot.Settings.Count}");
        Console.WriteLine($"  serializedSettings: {mirror.SerializedValues.Count}");
        Console.WriteLine($"  worldOptionSha256: {originalWorldOptionHash}");
        Console.WriteLine($"  originalIniSha256: {originalIniHash}");

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

        httpClient = new HttpClient
        {
            BaseAddress = new Uri($"http://127.0.0.1:{configuration.RestPort.Value}/", UriKind.Absolute),
            Timeout = TimeSpan.FromSeconds(5)
        };
        restClient = new PalworldRestApiClient(httpClient, "admin", transientPassword);

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

        EnsureSingleShippingProcessUsesExpectedExecutable(shippingExecutable);

        await AddFirewallRuleAsync(
            firewallRuleName,
            configuration.RestPort.Value,
            shippingExecutable);
        firewallRuleInstalled = await FirewallRuleExistsAsync(firewallRuleName);
        if (!firewallRuleInstalled)
        {
            throw new InvalidOperationException("The acceptance firewall rule was not present after creation.");
        }

        var loopbackInfo = await restClient.GetInfoAsync();
        loopbackRestWorksWithFirewall = string.Equals(
            loopbackInfo.WorldGuid,
            configuration.SelectedWorldId,
            StringComparison.OrdinalIgnoreCase);
        if (!loopbackRestWorksWithFirewall)
        {
            throw new InvalidDataException("Loopback REST no longer reported the selected World after the firewall rule was installed.");
        }

        Console.WriteLine();
        Console.WriteLine("firewallBoundary:");
        Console.WriteLine($"  ruleInstalled: {firewallRuleInstalled}");
        Console.WriteLine($"  loopbackRestWorks: {loopbackRestWorksWithFirewall}");
        Console.WriteLine("  scope: inbound Block, TCP, exact REST port, exact Shipping executable, all profiles");

        var lanEndpoints = GetLanIPv4Endpoints(configuration.RestPort.Value);
        if (lanEndpoints.Count == 0)
        {
            throw new InvalidOperationException(
                "No non-loopback IPv4 interface was available for a genuine second-device LAN test.");
        }

        Console.WriteLine();
        Console.WriteLine("LAN PEER TEST REQUIRED");
        Console.WriteLine("Do NOT test from this same PC. Use a genuinely separate device on the same LAN.");
        Console.WriteLine("Try one address that belongs to the LAN shared with that device:");
        foreach (var endpoint in lanEndpoints)
        {
            Console.WriteLine($"  {endpoint.InterfaceName}: {endpoint.Address}:{endpoint.Port}");
            Console.WriteLine($"    Windows peer: Test-NetConnection {endpoint.Address} -Port {endpoint.Port}");
            Console.WriteLine($"    curl peer:    curl --connect-timeout 5 http://{endpoint.Address}:{endpoint.Port}/v1/api/info");
        }

        Console.WriteLine();
        Console.WriteLine("PASS condition: the peer cannot establish TCP/HTTP. Any HTTP response, including 401, means the REST port is reachable.");
        Console.WriteLine("Type BLOCKED only after the separate peer is blocked; type REACHABLE if it can connect; type SKIP if no second peer is available.");
        Console.Write("> ");
        var peerVerdict = (Console.ReadLine() ?? string.Empty).Trim().ToUpperInvariant();
        switch (peerVerdict)
        {
            case "BLOCKED":
                lanPeerBlockOperatorConfirmed = true;
                break;
            case "REACHABLE":
                throw new InvalidDataException(
                    "A separate LAN peer could still reach the Palworld REST port with the acceptance firewall rule active.");
            default:
                throw new InvalidOperationException(
                    "LAN isolation was not proven by a genuine second-device test. The acceptance run will clean up without claiming success.");
        }

        await restClient.SaveAsync();
        saveSucceeded = true;

        await restClient.ShutdownAsync(1, "Steward REST firewall acceptance shutdown.");
        shutdownSucceeded = true;
        allProcessesExited = await WaitForPalworldExitAsync(launcher, TimeSpan.FromSeconds(60));
        if (!allProcessesExited)
        {
            throw new InvalidOperationException("Palworld process tree did not fully exit while the firewall rule remained active.");
        }

        await RemoveFirewallRuleAsync(firewallRuleName);
        firewallRuleRemovedAfterProcessExit = true;
        firewallRuleAbsentFinal = !await FirewallRuleExistsAsync(firewallRuleName);
        if (!firewallRuleAbsentFinal)
        {
            throw new InvalidOperationException("The acceptance firewall rule still exists after removal.");
        }

        unexpectedWorldOptionGenerated = File.Exists(worldOptionPath);
        if (unexpectedWorldOptionGenerated)
        {
            throw new InvalidDataException(
                "Palworld generated WorldOption.sav while the canonical file was parked; the canonical file will be recovered, but this run is not accepted.");
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
        TimeoutException or
        DllNotFoundException or
        BadImageFormatException or
        EntryPointNotFoundException or
        ArgumentException or
        OverflowException or
        System.ComponentModel.Win32Exception)
    {
        Console.Error.WriteLine($"REST firewall acceptance failed: {exception.Message}");
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
                    if (restClient is not null)
                    {
                        try
                        {
                            if (!saveSucceeded)
                            {
                                await restClient.SaveAsync(CancellationToken.None);
                                saveSucceeded = true;
                            }

                            if (!shutdownSucceeded)
                            {
                                await restClient.ShutdownAsync(
                                    0,
                                    "Steward REST firewall acceptance cleanup.",
                                    CancellationToken.None);
                                shutdownSucceeded = true;
                            }

                            allProcessesExited = await WaitForPalworldExitAsync(
                                launcher,
                                TimeSpan.FromSeconds(20));
                        }
                        catch
                        {
                            // Acceptance cleanup may have started before REST was usable.
                        }
                    }

                    if (IsAnyPalworldProcessRunning())
                    {
                        forcedCleanupUsed = await ForceCleanupPalworldProcessesAsync();
                    }
                }

                allProcessesExited = !IsAnyPalworldProcessRunning();
            }
            catch (InvalidOperationException)
            {
                allProcessesExited = !IsAnyPalworldProcessRunning();
            }
            finally
            {
                launcher.Dispose();
            }
        }

        if (!IsAnyPalworldProcessRunning())
        {
            try
            {
                if (await FirewallRuleExistsAsync(firewallRuleName))
                {
                    await RemoveFirewallRuleAsync(firewallRuleName);
                    firewallRuleRemovedAfterProcessExit = true;
                }

                firewallRuleAbsentFinal = !await FirewallRuleExistsAsync(firewallRuleName);
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine(
                    $"CRITICAL: Palworld exited but the acceptance firewall rule could not be cleaned up: {exception.Message}");
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
        }
        else
        {
            Console.Error.WriteLine(
                $"CRITICAL: a Palworld process is still running. Steward deliberately left firewall rule '{firewallRuleName}' in place and did not restore canonical runtime inputs underneath the live process.");
        }

        httpClient?.Dispose();
        CryptographicOperations.ZeroMemory(originalWorldOptionBytes);
        CryptographicOperations.ZeroMemory(originalIniBytes);
        transientPassword = string.Empty;
    }

    var proven = administratorPreflightSucceeded &&
        readinessSucceeded &&
        worldGuidMatched &&
        firewallRuleInstalled &&
        loopbackRestWorksWithFirewall &&
        lanPeerBlockOperatorConfirmed &&
        saveSucceeded &&
        shutdownSucceeded &&
        allProcessesExited &&
        firewallRuleRemovedAfterProcessExit &&
        firewallRuleAbsentFinal &&
        !forcedCleanupUsed &&
        !unexpectedWorldOptionGenerated &&
        originalsRestoredAfterProcessExit &&
        finalWorldOptionHashMatches &&
        finalIniHashMatches &&
        transientCredentialAbsentFromRestoredFiles;

    Console.WriteLine();
    Console.WriteLine("result:");
    Console.WriteLine($"  administratorPreflightSucceeded: {administratorPreflightSucceeded}");
    Console.WriteLine($"  readinessSucceeded: {readinessSucceeded}");
    Console.WriteLine($"  worldGuidMatched: {worldGuidMatched}");
    Console.WriteLine($"  firewallRuleInstalled: {firewallRuleInstalled}");
    Console.WriteLine($"  loopbackRestWorksWithFirewall: {loopbackRestWorksWithFirewall}");
    Console.WriteLine($"  lanPeerBlockOperatorConfirmed: {lanPeerBlockOperatorConfirmed}");
    Console.WriteLine($"  saveSucceeded: {saveSucceeded}");
    Console.WriteLine($"  shutdownSucceeded: {shutdownSucceeded}");
    Console.WriteLine($"  allPalworldProcessesExited: {allProcessesExited}");
    Console.WriteLine($"  firewallRuleRemovedAfterProcessExit: {firewallRuleRemovedAfterProcessExit}");
    Console.WriteLine($"  firewallRuleAbsentFinal: {firewallRuleAbsentFinal}");
    Console.WriteLine($"  unexpectedWorldOptionGenerated: {unexpectedWorldOptionGenerated}");
    Console.WriteLine($"  originalsRestoredAfterProcessExit: {originalsRestoredAfterProcessExit}");
    Console.WriteLine($"  forcedCleanupUsed: {forcedCleanupUsed}");
    Console.WriteLine($"  finalWorldOptionSha256Matches: {finalWorldOptionHashMatches}");
    Console.WriteLine($"  finalPalWorldSettingsIniSha256Matches: {finalIniHashMatches}");
    Console.WriteLine($"  transientCredentialAbsentFromRestoredFiles: {transientCredentialAbsentFromRestoredFiles}");
    Console.WriteLine($"restFirewallBoundaryProven: {proven}");

    if (!firewallRuleAbsentFinal)
    {
        Console.WriteLine($"manualFirewallRuleName: {firewallRuleName}");
    }

    return proven;
}

static async Task<bool> IsAdministratorAsync()
{
    const string script =
        "$identity=[Security.Principal.WindowsIdentity]::GetCurrent();" +
        "$principal=New-Object Security.Principal.WindowsPrincipal($identity);" +
        "if($principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)){exit 0}else{exit 5}";
    var result = await RunPowerShellAsync(script);
    return result.ExitCode == 0;
}

static async Task<IReadOnlyList<string>> FindStaleFirewallRulesAsync()
{
    var script =
        "$ErrorActionPreference='Stop';" +
        $"$rules=@(Get-NetFirewallRule -Name {PowerShellLiteral(FirewallRulePrefix + "*")} -ErrorAction SilentlyContinue);" +
        "if($rules.Count -gt 0){$rules | ForEach-Object {$_.Name}; exit 9}";
    var result = await RunPowerShellAsync(script);
    if (result.ExitCode == 0)
    {
        return Array.Empty<string>();
    }

    if (result.ExitCode == 9)
    {
        return result.StandardOutput
            .Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    throw new InvalidOperationException(
        $"Could not inspect existing Windows Firewall rules: {UsefulPowerShellError(result)}");
}

static async Task AddFirewallRuleAsync(string ruleName, int port, string programPath)
{
    var description =
        "Temporary Steward acceptance rule: block non-loopback inbound access to the Palworld REST listener during the managed-session firewall experiment.";
    var script =
        "$ErrorActionPreference='Stop';" +
        $"New-NetFirewallRule -Name {PowerShellLiteral(ruleName)} -DisplayName {PowerShellLiteral(ruleName)} " +
        $"-Description {PowerShellLiteral(description)} -Direction Inbound -Action Block -Enabled True -Profile Any " +
        $"-Protocol TCP -LocalPort {port} -Program {PowerShellLiteral(programPath)} | Out-Null;" +
        $"$rule=Get-NetFirewallRule -Name {PowerShellLiteral(ruleName)} -ErrorAction Stop;" +
        "$app=$rule | Get-NetFirewallApplicationFilter;" +
        "$portFilter=$rule | Get-NetFirewallPortFilter;" +
        $"if([string]$rule.Direction -ne 'Inbound' -or [string]$rule.Action -ne 'Block' -or [string]$rule.Enabled -ne 'True' " +
        $"-or $app.Program -ine {PowerShellLiteral(programPath)} -or [string]$portFilter.Protocol -ne 'TCP' " +
        $"-or [string]$portFilter.LocalPort -ne {PowerShellLiteral(port.ToString())}){{throw 'Created firewall rule did not match the requested scope.'}}";

    var result = await RunPowerShellAsync(script);
    if (result.ExitCode != 0)
    {
        throw new InvalidOperationException(
            $"Could not create/verify the temporary Windows Firewall rule: {UsefulPowerShellError(result)}");
    }
}

static async Task RemoveFirewallRuleAsync(string ruleName)
{
    var script =
        "$ErrorActionPreference='Stop';" +
        $"$rule=Get-NetFirewallRule -Name {PowerShellLiteral(ruleName)} -ErrorAction SilentlyContinue;" +
        "if($null -ne $rule){$rule | Remove-NetFirewallRule -ErrorAction Stop};" +
        $"if($null -ne (Get-NetFirewallRule -Name {PowerShellLiteral(ruleName)} -ErrorAction SilentlyContinue))" +
        "{throw 'Firewall rule still exists after removal.'}";
    var result = await RunPowerShellAsync(script);
    if (result.ExitCode != 0)
    {
        throw new InvalidOperationException(
            $"Could not remove/verify the temporary Windows Firewall rule: {UsefulPowerShellError(result)}");
    }
}

static async Task<bool> FirewallRuleExistsAsync(string ruleName)
{
    var script =
        $"if($null -ne (Get-NetFirewallRule -Name {PowerShellLiteral(ruleName)} -ErrorAction SilentlyContinue))" +
        "{exit 0}else{exit 4}";
    var result = await RunPowerShellAsync(script);
    return result.ExitCode switch
    {
        0 => true,
        4 => false,
        _ => throw new InvalidOperationException(
            $"Could not inspect the temporary Windows Firewall rule: {UsefulPowerShellError(result)}")
    };
}

static async Task<PowerShellResult> RunPowerShellAsync(string script)
{
    var startInfo = new ProcessStartInfo
    {
        FileName = "powershell.exe",
        UseShellExecute = false,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        CreateNoWindow = true
    };
    startInfo.ArgumentList.Add("-NoLogo");
    startInfo.ArgumentList.Add("-NoProfile");
    startInfo.ArgumentList.Add("-NonInteractive");
    startInfo.ArgumentList.Add("-Command");
    startInfo.ArgumentList.Add(script);

    using var process = Process.Start(startInfo)
        ?? throw new InvalidOperationException("Windows PowerShell could not be started for firewall management.");
    var standardOutputTask = process.StandardOutput.ReadToEndAsync();
    var standardErrorTask = process.StandardError.ReadToEndAsync();
    await process.WaitForExitAsync();
    return new PowerShellResult(
        process.ExitCode,
        await standardOutputTask,
        await standardErrorTask);
}

static string PowerShellLiteral(string value)
    => "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";

static string UsefulPowerShellError(PowerShellResult result)
{
    var error = result.StandardError.Trim();
    if (!string.IsNullOrWhiteSpace(error))
    {
        return error;
    }

    var output = result.StandardOutput.Trim();
    return string.IsNullOrWhiteSpace(output)
        ? $"PowerShell exited with code {result.ExitCode}."
        : output;
}

static void EnsureSingleShippingProcessUsesExpectedExecutable(string expectedExecutable)
{
    var active = new List<Process>();
    try
    {
        foreach (var process in Process.GetProcessesByName(ShippingProcessName))
        {
            try
            {
                if (!process.HasExited)
                {
                    active.Add(process);
                }
                else
                {
                    process.Dispose();
                }
            }
            catch (InvalidOperationException)
            {
                process.Dispose();
            }
        }

        if (active.Count != 1)
        {
            throw new InvalidOperationException(
                $"Expected exactly one active {ShippingProcessName} process after REST readiness, found {active.Count}.");
        }

        try
        {
            var actualExecutable = active[0].MainModule?.FileName;
            if (!string.IsNullOrWhiteSpace(actualExecutable) &&
                !string.Equals(
                    Path.GetFullPath(actualExecutable),
                    Path.GetFullPath(expectedExecutable),
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    $"The active Shipping process executable '{actualExecutable}' did not match the discovered Palworld executable '{expectedExecutable}'.");
            }
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // The exact discovered path remains the firewall Program scope; the separate LAN test is
            // authoritative if Windows denies process-module inspection despite elevation.
        }
    }
    finally
    {
        foreach (var process in active)
        {
            process.Dispose();
        }
    }
}

static IReadOnlyList<LanEndpoint> GetLanIPv4Endpoints(int port)
{
    var endpoints = new List<LanEndpoint>();
    foreach (var networkInterface in NetworkInterface.GetAllNetworkInterfaces())
    {
        if (networkInterface.OperationalStatus != OperationalStatus.Up ||
            networkInterface.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
        {
            continue;
        }

        foreach (var unicast in networkInterface.GetIPProperties().UnicastAddresses)
        {
            var address = unicast.Address;
            if (address.AddressFamily != AddressFamily.InterNetwork ||
                IPAddress.IsLoopback(address) ||
                IsLinkLocalIPv4(address))
            {
                continue;
            }

            endpoints.Add(new LanEndpoint(networkInterface.Name, address, port));
        }
    }

    return endpoints
        .GroupBy(endpoint => endpoint.Address.ToString(), StringComparer.Ordinal)
        .Select(group => group.First())
        .OrderBy(endpoint => endpoint.InterfaceName, StringComparer.OrdinalIgnoreCase)
        .ThenBy(endpoint => endpoint.Address.ToString(), StringComparer.Ordinal)
        .ToArray();
}

static bool IsLinkLocalIPv4(IPAddress address)
{
    var bytes = address.GetAddressBytes();
    return bytes.Length == 4 && bytes[0] == 169 && bytes[1] == 254;
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
                    // Final process observation and firewall retention decide safety.
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

internal sealed record PowerShellResult(
    int ExitCode,
    string StandardOutput,
    string StandardError);

internal sealed record LanEndpoint(
    string InterfaceName,
    IPAddress Address,
    int Port);
