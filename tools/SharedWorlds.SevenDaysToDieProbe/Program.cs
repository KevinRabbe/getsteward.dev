using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using SharedWorlds.Core.Abstractions;
using SharedWorlds.GameAdapters.SevenDaysToDie;

const int UsageError = 2;
var helpRequested = args.Any(argument =>
    string.Equals(argument, "--help", StringComparison.OrdinalIgnoreCase) ||
    string.Equals(argument, "-h", StringComparison.OrdinalIgnoreCase));

if (helpRequested)
{
    PrintUsage();
    return;
}

using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    cancellation.Cancel();
};

var adapter = new SevenDaysToDieAdapter();
if (args.Length == 0 ||
    (args.Length == 1 && string.Equals(args[0], "--list", StringComparison.OrdinalIgnoreCase)))
{
    await PrintDiscoveryAsync(adapter, cancellation.Token);
    return;
}

if (args.Length != 2 ||
    !string.Equals(args[0], "--lifecycle-acceptance", StringComparison.OrdinalIgnoreCase) ||
    string.IsNullOrWhiteSpace(args[1]))
{
    PrintUsage();
    Environment.ExitCode = UsageError;
    return;
}

var selector = args[1].Trim();
var selection = await FindWorldAsync(adapter, selector, cancellation.Token);
if (selection is null)
{
    Console.Error.WriteLine($"No unique 7 Days to Die World matched '{selector}'. Run the probe with --list first.");
    Environment.ExitCode = 1;
    return;
}

var installation = selection.Installation;
var detectedWorld = selection.World;
var metadata = installation.Metadata ?? throw new InvalidOperationException(
    "The selected 7 Days to Die installation has no discovery metadata.");
var dedicatedServerRoot = GetRequiredMetadata(
    metadata,
    SevenDaysToDieInstallationDiscovery.DedicatedServerRootPathKey,
    "dedicated server root");
var dedicatedServerExecutable = GetRequiredMetadata(
    metadata,
    SevenDaysToDieInstallationDiscovery.DedicatedServerExecutablePathKey,
    "dedicated server executable");
var sourceServerConfig = Path.Combine(dedicatedServerRoot, "serverconfig.xml");
if (!File.Exists(dedicatedServerExecutable))
{
    throw new FileNotFoundException(
        "The discovered 7 Days to Die Dedicated Server executable no longer exists.",
        dedicatedServerExecutable);
}

if (!File.Exists(sourceServerConfig))
{
    throw new FileNotFoundException(
        "The 7 Days to Die Dedicated Server serverconfig.xml is required for lifecycle acceptance.",
        sourceServerConfig);
}

var identity = GetWorldIdentity(installation, detectedWorld);
var environment = await adapter.InspectEnvironmentAsync(installation, detectedWorld, cancellation.Token);
var verification = await adapter.VerifyEnvironmentAsync(installation, environment, cancellation.Token);
if (!verification.IsReady)
{
    Console.Error.WriteLine("The selected 7 Days to Die environment is not reproducible on this machine:");
    foreach (var issue in verification.Issues)
    {
        Console.Error.WriteLine($"  - {issue.Message}");
    }

    Environment.ExitCode = 1;
    return;
}

var evidenceRoot = Path.Combine(
    Path.GetTempPath(),
    $"sharedworlds-7dtd-acceptance-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}");
Directory.CreateDirectory(evidenceRoot);

Console.WriteLine("SharedWorlds 7 Days to Die lifecycle acceptance");
Console.WriteLine();
Console.WriteLine($"Client installation: {installation.RootPath}");
Console.WriteLine($"Dedicated server: {dedicatedServerExecutable}");
Console.WriteLine($"Dedicated build: {environment.GameVersion}");
Console.WriteLine($"Selected World: {detectedWorld.DisplayName}");
Console.WriteLine($"GameWorld: {identity.WorldName}");
Console.WriteLine($"GameName: {identity.GameName}");
Console.WriteLine($"Source World: {detectedWorld.SourcePath}");
Console.WriteLine($"Evidence root: {evidenceRoot}");
Console.WriteLine();
Console.WriteLine("The source World is never launched or modified. The probe first captures it, restores that package into an isolated Steward workspace, and launches only the disposable copy.");
Console.WriteLine();

CapturedState? sourceCapture = null;
PreparedWorld? firstPrepared = null;
PreparedWorld? secondPrepared = null;
try
{
    sourceCapture = await adapter.CaptureDetectedWorldAsync(
        installation,
        detectedWorld,
        cancellation.Token);
    Console.WriteLine($"Initial source snapshot: {sourceCapture.Package.Path}");

    firstPrepared = await adapter.PrepareEnvironmentAsync(
        installation,
        environment,
        cancellation.Token);
    await adapter.RestoreStateAsync(
        firstPrepared,
        sourceCapture.Package,
        cancellation.Token);

    Console.WriteLine();
    Console.WriteLine("First disposable launch");
    var firstRun = await RunDisposableServerAsync(
        dedicatedServerExecutable,
        dedicatedServerRoot,
        sourceServerConfig,
        firstPrepared,
        identity,
        evidenceRoot,
        "first",
        "When the World is visibly loaded, make one disposable in-game change if practical. Then return here and press Enter. The probe will send the documented shutdown command.",
        cancellation.Token);
    if (!firstRun.Success)
    {
        Console.Error.WriteLine("First disposable lifecycle did not satisfy the acceptance barrier. The isolated workspace is being preserved for inspection.");
        Environment.ExitCode = 1;
        return;
    }

    var capturedAfterFirstRun = await adapter.CaptureStateAsync(
        firstPrepared,
        cancellation.Token);
    Console.WriteLine($"Captured post-shutdown package: {capturedAfterFirstRun.Package.Path}");

    await adapter.FinalizePreparedWorldAsync(
        firstPrepared,
        PreparedWorldDisposition.Discard,
        cancellation.Token);
    firstPrepared = null;

    secondPrepared = await adapter.PrepareEnvironmentAsync(
        installation,
        environment,
        cancellation.Token);
    await adapter.RestoreStateAsync(
        secondPrepared,
        capturedAfterFirstRun.Package,
        cancellation.Token);

    Console.WriteLine();
    Console.WriteLine("Captured-state relaunch");
    var secondRun = await RunDisposableServerAsync(
        dedicatedServerExecutable,
        dedicatedServerRoot,
        sourceServerConfig,
        secondPrepared,
        identity,
        evidenceRoot,
        "relaunch",
        "Reconnect to the disposable server and verify that the captured World, including the change from the first run if you made one, loads correctly. Then press Enter to shut it down cleanly.",
        cancellation.Token);
    if (!secondRun.Success)
    {
        Console.Error.WriteLine("The captured-state relaunch did not satisfy the acceptance barrier. The second isolated workspace is being preserved for inspection.");
        Environment.ExitCode = 1;
        return;
    }

    await adapter.FinalizePreparedWorldAsync(
        secondPrepared,
        PreparedWorldDisposition.Discard,
        cancellation.Token);
    secondPrepared = null;

    Console.WriteLine();
    Console.WriteLine("Lifecycle acceptance completed.");
    Console.WriteLine("Proven by this run:");
    Console.WriteLine("  restored named World launched from an adapter-owned isolated user-data tree");
    Console.WriteLine("  managed Telnet listener was observed on loopback only");
    Console.WriteLine("  documented raw/Telnet shutdown command caused the owned server process to exit without forced termination");
    Console.WriteLine("  capture happened only after process exit");
    Console.WriteLine("  the captured package was restored and relaunched successfully");
    Console.WriteLine();
    Console.WriteLine("Not promoted by this probe alone:");
    Console.WriteLine("  AutomaticHostLaunch, AutomaticHostStop, or automatic Join capabilities");
    Console.WriteLine("  a production World-ready signal; use the retained server logs to identify one only from observed real-game evidence");
    Console.WriteLine();
    Console.WriteLine($"Retained captured package: {capturedAfterFirstRun.Package.Path}");
    Console.WriteLine($"Retained logs/config evidence: {evidenceRoot}");
}
finally
{
    TryDeleteFile(sourceCapture?.Package.Path);

    if (firstPrepared is not null)
    {
        await adapter.FinalizePreparedWorldAsync(
            firstPrepared,
            PreparedWorldDisposition.PreserveForRecovery,
            CancellationToken.None);
        Console.WriteLine($"Preserved first workspace for inspection: {firstPrepared.WorkingDirectory}");
    }

    if (secondPrepared is not null)
    {
        await adapter.FinalizePreparedWorldAsync(
            secondPrepared,
            PreparedWorldDisposition.PreserveForRecovery,
            CancellationToken.None);
        Console.WriteLine($"Preserved relaunch workspace for inspection: {secondPrepared.WorkingDirectory}");
    }
}

static void PrintUsage()
{
    Console.WriteLine("SharedWorlds 7 Days to Die acceptance probe");
    Console.WriteLine();
    Console.WriteLine("Usage:");
    Console.WriteLine("  dotnet run --project tools/SharedWorlds.SevenDaysToDieProbe -- --list");
    Console.WriteLine("  dotnet run --project tools/SharedWorlds.SevenDaysToDieProbe -- --lifecycle-acceptance \"<World display name or ID>\"");
    Console.WriteLine();
    Console.WriteLine("The lifecycle mode uses only a disposable captured copy. It does not grant 7DTD runtime capabilities.");
}

static async Task PrintDiscoveryAsync(
    SevenDaysToDieAdapter adapter,
    CancellationToken cancellationToken)
{
    var installations = await adapter.DiscoverInstallationsAsync(cancellationToken);
    Console.WriteLine("SharedWorlds 7 Days to Die probe (read-only preflight)");
    Console.WriteLine();
    if (installations.Count == 0)
    {
        Console.WriteLine("No 7 Days to Die client installation was detected.");
        return;
    }

    foreach (var installation in installations)
    {
        Console.WriteLine($"Client installation: {installation.RootPath}");
        if (installation.Metadata is not null)
        {
            foreach (var entry in installation.Metadata.OrderBy(entry => entry.Key, StringComparer.Ordinal))
            {
                Console.WriteLine($"  {entry.Key}: {entry.Value}");
            }
        }

        var worlds = await adapter.DiscoverWorldsAsync(installation, cancellationToken);
        Console.WriteLine($"  detected Worlds: {worlds.Count}");
        foreach (var world in worlds)
        {
            Console.WriteLine($"    - {world.DisplayName}");
            Console.WriteLine($"      ID: {world.Id}");
            Console.WriteLine($"      Path: {world.SourcePath}");
        }

        Console.WriteLine();
    }
}

static async Task<WorldSelection?> FindWorldAsync(
    SevenDaysToDieAdapter adapter,
    string selector,
    CancellationToken cancellationToken)
{
    var matches = new List<WorldSelection>();
    var installations = await adapter.DiscoverInstallationsAsync(cancellationToken);
    foreach (var installation in installations)
    {
        var worlds = await adapter.DiscoverWorldsAsync(installation, cancellationToken);
        foreach (var world in worlds)
        {
            if (string.Equals(world.Id, selector, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(world.DisplayName, selector, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(world.SourcePath, selector, StringComparison.OrdinalIgnoreCase))
            {
                matches.Add(new WorldSelection(installation, world));
            }
        }
    }

    return matches.Count == 1 ? matches[0] : null;
}

static WorldIdentity GetWorldIdentity(
    GameInstallation installation,
    DetectedWorld world)
{
    var metadata = installation.Metadata ?? throw new InvalidOperationException(
        "The selected 7 Days to Die installation has no discovery metadata.");
    var userDataRoot = Path.GetFullPath(GetRequiredMetadata(
        metadata,
        SevenDaysToDieInstallationDiscovery.UserDataPathKey,
        "user-data root"));
    var sourcePath = Path.GetFullPath(world.SourcePath);
    var relativePath = Path.GetRelativePath(userDataRoot, sourcePath);
    var segments = relativePath.Split(
        [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
        StringSplitOptions.RemoveEmptyEntries);
    if (segments.Length != 3 ||
        !string.Equals(segments[0], "Saves", StringComparison.OrdinalIgnoreCase) ||
        string.IsNullOrWhiteSpace(segments[1]) ||
        string.IsNullOrWhiteSpace(segments[2]))
    {
        throw new InvalidOperationException(
            "The selected 7 Days to Die World is not beneath Saves/<GameWorld>/<GameName>.");
    }

    return new WorldIdentity(segments[1], segments[2]);
}

static string GetRequiredMetadata(
    IReadOnlyDictionary<string, string> metadata,
    string key,
    string description)
{
    if (!metadata.TryGetValue(key, out var value) || string.IsNullOrWhiteSpace(value))
    {
        throw new InvalidOperationException($"The 7 Days to Die {description} was not discovered on this machine.");
    }

    return Path.GetFullPath(value);
}

static async Task<DisposableRunResult> RunDisposableServerAsync(
    string serverExecutable,
    string serverRoot,
    string sourceServerConfig,
    PreparedWorld preparedWorld,
    WorldIdentity identity,
    string evidenceRoot,
    string runLabel,
    string userPrompt,
    CancellationToken cancellationToken)
{
    var telnetPort = ReserveLoopbackPort();
    var control = new SevenDaysToDieManagedControl(telnetPort);
    var sourceBytes = await File.ReadAllBytesAsync(sourceServerConfig, cancellationToken);
    var transformed = SevenDaysToDieManagedServerConfiguration.TransformForManagedHost(
        sourceBytes,
        preparedWorld.WorkingDirectory,
        identity.WorldName,
        identity.GameName,
        control);
    var acceptanceConfig = CreateAcceptanceConfig(transformed);
    var configPath = Path.Combine(evidenceRoot, $"serverconfig-{runLabel}.xml");
    var logPath = Path.Combine(evidenceRoot, $"server-{runLabel}.log");
    await File.WriteAllBytesAsync(configPath, acceptanceConfig.Bytes, cancellationToken);

    var startInfo = new ProcessStartInfo
    {
        FileName = serverExecutable,
        WorkingDirectory = serverRoot,
        UseShellExecute = false,
        CreateNoWindow = true
    };
    startInfo.ArgumentList.Add("-quit");
    startInfo.ArgumentList.Add("-batchmode");
    startInfo.ArgumentList.Add("-nographics");
    startInfo.ArgumentList.Add($"-configfile={configPath}");
    startInfo.ArgumentList.Add("-logfile");
    startInfo.ArgumentList.Add(logPath);
    startInfo.ArgumentList.Add("-dedicated");

    using var process = Process.Start(startInfo) ?? throw new InvalidOperationException(
        "7 Days to Die Dedicated Server could not be started.");
    var forcedCleanupUsed = false;
    try
    {
        Console.WriteLine($"  PID: {process.Id}");
        Console.WriteLine($"  UserDataFolder: {preparedWorld.WorkingDirectory}");
        Console.WriteLine($"  config: {configPath}");
        Console.WriteLine($"  log: {logPath}");
        Console.WriteLine($"  Telnet: 127.0.0.1:{telnetPort}");
        Console.WriteLine($"  game port: {acceptanceConfig.ServerPort ?? "(from game/default)"}");
        Console.WriteLine($"  temporary game password: {acceptanceConfig.GamePassword}");

        var listenerReady = await WaitForLoopbackListenerAsync(
            process,
            telnetPort,
            TimeSpan.FromMinutes(5),
            cancellationToken);
        if (!listenerReady)
        {
            Console.Error.WriteLine("  Telnet listener did not become reachable before the bounded timeout or the server exited early.");
            PrintLogTail(logPath);
            return new DisposableRunResult(false, false, false, false);
        }

        var listener = ObserveListener(telnetPort);
        Console.WriteLine($"  Telnet binding: {(listener.Endpoints.Count == 0 ? "(none)" : string.Join(", ", listener.Endpoints))}");
        Console.WriteLine($"  Telnet loopback-only: {listener.LoopbackOnly}");
        if (!listener.LoopbackOnly)
        {
            Console.Error.WriteLine("  The game-documented empty-password loopback boundary was not observed. The probe will shut down this disposable server and fail the run.");
            await SendShutdownAsync(telnetPort, cancellationToken);
            var exitedAfterMismatch = await WaitForExitAsync(
                process,
                TimeSpan.FromMinutes(5),
                cancellationToken);
            return new DisposableRunResult(false, true, true, exitedAfterMismatch);
        }

        Console.WriteLine();
        Console.WriteLine("  Management transport is reachable. This is deliberately not treated as a production World-ready signal.");
        Console.WriteLine($"  {userPrompt}");
        _ = Console.ReadLine();
        cancellationToken.ThrowIfCancellationRequested();

        Console.WriteLine("  Sending documented 'shutdown' command over the loopback service interface...");
        await SendShutdownAsync(telnetPort, cancellationToken);
        var processExited = await WaitForExitAsync(
            process,
            TimeSpan.FromMinutes(5),
            cancellationToken);
        Console.WriteLine($"  process exited: {processExited}");
        if (!processExited)
        {
            Console.Error.WriteLine("  Graceful shutdown did not produce complete owned-process exit before the bounded timeout.");
            PrintLogTail(logPath);
            return new DisposableRunResult(false, true, true, false);
        }

        return new DisposableRunResult(true, true, true, true);
    }
    finally
    {
        if (!process.HasExited)
        {
            process.Kill(entireProcessTree: true);
            forcedCleanupUsed = true;
            await process.WaitForExitAsync(CancellationToken.None);
        }

        if (forcedCleanupUsed)
        {
            Console.WriteLine("  forced cleanup used: true (acceptance failed; only the disposable process tree was terminated)");
        }
    }
}

static AcceptanceConfig CreateAcceptanceConfig(byte[] transformed)
{
    var offset = transformed.Length >= 3 &&
        transformed[0] == 0xEF &&
        transformed[1] == 0xBB &&
        transformed[2] == 0xBF
        ? 3
        : 0;
    var text = new UTF8Encoding(false, true).GetString(transformed, offset, transformed.Length - offset);
    var document = XDocument.Parse(text, LoadOptions.PreserveWhitespace);
    var root = document.Root ?? throw new InvalidDataException(
        "The transformed 7 Days to Die acceptance configuration has no root element.");
    var password = "st_accept_" + Convert.ToHexString(RandomNumberGenerator.GetBytes(18)).ToLowerInvariant();
    SetServerProperty(root, "ServerVisibility", "0");
    SetServerProperty(root, "ServerPassword", password);
    SetServerProperty(root, "ServerName", "Steward disposable acceptance");
    SetServerProperty(root, "ControlPanelEnabled", "false");
    var serverPort = ReadServerProperty(root, "ServerPort");
    return new AcceptanceConfig(
        Encoding.UTF8.GetBytes(document.ToString(SaveOptions.DisableFormatting)),
        password,
        serverPort);
}

static void SetServerProperty(XElement root, string name, string value)
{
    var matches = root
        .Elements()
        .Where(element =>
            string.Equals(element.Name.LocalName, "property", StringComparison.Ordinal) &&
            string.Equals(element.Attribute("name")?.Value, name, StringComparison.Ordinal))
        .Take(2)
        .ToArray();
    if (matches.Length > 1)
    {
        throw new InvalidDataException(
            $"7 Days to Die acceptance configuration contains duplicate active {name} properties.");
    }

    var property = matches.SingleOrDefault();
    if (property is null)
    {
        root.Add(new XElement("property", new XAttribute("name", name), new XAttribute("value", value)));
        return;
    }

    property.SetAttributeValue("value", value);
}

static string? ReadServerProperty(XElement root, string name)
    => root
        .Elements()
        .SingleOrDefault(element =>
            string.Equals(element.Name.LocalName, "property", StringComparison.Ordinal) &&
            string.Equals(element.Attribute("name")?.Value, name, StringComparison.Ordinal))
        ?.Attribute("value")
        ?.Value;

static int ReserveLoopbackPort()
{
    var listener = new TcpListener(IPAddress.Loopback, 0);
    listener.Start();
    try
    {
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
    finally
    {
        listener.Stop();
    }
}

static async Task<bool> WaitForLoopbackListenerAsync(
    Process process,
    int port,
    TimeSpan timeout,
    CancellationToken cancellationToken)
{
    var deadline = DateTimeOffset.UtcNow + timeout;
    while (DateTimeOffset.UtcNow < deadline)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (process.HasExited)
        {
            return false;
        }

        using var client = new TcpClient(AddressFamily.InterNetwork);
        using var attempt = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        attempt.CancelAfter(TimeSpan.FromSeconds(2));
        try
        {
            await client.ConnectAsync(IPAddress.Loopback, port, attempt.Token);
            return true;
        }
        catch (SocketException)
        {
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
        }

        await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken);
    }

    return false;
}

static ListenerObservation ObserveListener(int port)
{
    var endpoints = IPGlobalProperties.GetIPGlobalProperties()
        .GetActiveTcpListeners()
        .Where(endpoint => endpoint.Port == port)
        .OrderBy(endpoint => endpoint.Address.ToString(), StringComparer.Ordinal)
        .ThenBy(endpoint => endpoint.Port)
        .ToArray();
    return new ListenerObservation(
        endpoints.Select(endpoint => endpoint.ToString()).ToArray(),
        endpoints.Length > 0 && endpoints.All(endpoint => IPAddress.IsLoopback(endpoint.Address)));
}

static async Task SendShutdownAsync(int port, CancellationToken cancellationToken)
{
    using var client = new TcpClient(AddressFamily.InterNetwork);
    await client.ConnectAsync(IPAddress.Loopback, port, cancellationToken);
    await using var stream = client.GetStream();
    var command = Encoding.ASCII.GetBytes("shutdown\r\n");
    await stream.WriteAsync(command, cancellationToken);
    await stream.FlushAsync(cancellationToken);
}

static async Task<bool> WaitForExitAsync(
    Process process,
    TimeSpan timeout,
    CancellationToken cancellationToken)
{
    if (process.HasExited)
    {
        return true;
    }

    using var wait = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
    wait.CancelAfter(timeout);
    try
    {
        await process.WaitForExitAsync(wait.Token);
        return true;
    }
    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
    {
        return process.HasExited;
    }
}

static void PrintLogTail(string logPath)
{
    if (!File.Exists(logPath))
    {
        Console.WriteLine("  log tail: log file not created");
        return;
    }

    Console.WriteLine("  log tail:");
    foreach (var line in File.ReadLines(logPath).TakeLast(30))
    {
        Console.WriteLine($"    {line}");
    }
}

static void TryDeleteFile(string? path)
{
    if (string.IsNullOrWhiteSpace(path))
    {
        return;
    }

    try
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }
    catch (IOException)
    {
    }
    catch (UnauthorizedAccessException)
    {
    }
}

internal sealed record WorldSelection(
    GameInstallation Installation,
    DetectedWorld World);

internal sealed record WorldIdentity(
    string WorldName,
    string GameName);

internal sealed record AcceptanceConfig(
    byte[] Bytes,
    string GamePassword,
    string? ServerPort);

internal sealed record ListenerObservation(
    IReadOnlyList<string> Endpoints,
    bool LoopbackOnly);

internal sealed record DisposableRunResult(
    bool Success,
    bool ListenerReady,
    bool ShutdownSent,
    bool ProcessExited);
