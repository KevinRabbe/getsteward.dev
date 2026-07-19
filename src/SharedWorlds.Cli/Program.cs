using SharedWorlds.Cli;
using SharedWorlds.Core.Abstractions;
using SharedWorlds.Core.Domain;
using SharedWorlds.Core.Worlds;
using SharedWorlds.GameAdapters.Factorio;
using SharedWorlds.GameAdapters.ProjectZomboid;
using SharedWorlds.GameAdapters.SevenDaysToDie;
using SharedWorlds.Infrastructure.Sessions;
using SharedWorlds.Infrastructure.Storage;

var localDataRoot = GetLocalDataRoot();
var diagnosticsRoot = Path.Combine(localDataRoot, "SharedWorlds", "logs");
using var cancellation = new CancellationTokenSource();

Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    cancellation.Cancel();
};

try
{
    return await RunAsync(args, localDataRoot, cancellation.Token);
}
catch (Exception exception)
{
    return await ConsoleExceptionHandler.HandleAsync(exception, diagnosticsRoot);
}

static async Task<int> RunAsync(
    string[] arguments,
    string localDataRoot,
    CancellationToken cancellationToken)
{
    IGameAdapter[] adapters =
    [
        new FactorioAdapter(),
        new SevenDaysToDieAdapter(),
        new ProjectZomboidAdapter()
    ];

    var storageRoot = Path.Combine(
        localDataRoot,
        "SharedWorlds",
        "data");

    var storage = new LocalWorldStorage(storageRoot);
    var sessions = new LocalWorldSessionCoordinator();
    var recovery = new LocalWorkspaceRecoveryStore(storageRoot);
    var lifecycle = new WorldLifecycleService(storage, sessions, recovery);

    if (arguments.Length == 0 ||
        string.Equals(arguments[0], "discover", StringComparison.OrdinalIgnoreCase))
    {
        await DiscoverAsync(adapters, cancellationToken);
        return ApplicationExitCodes.Success;
    }

    return arguments[0].ToLowerInvariant() switch
    {
        "import-factorio" => await ImportFactorioAsync(arguments, lifecycle, cancellationToken),
        "continue-factorio" => await ContinueFactorioAsync(arguments, lifecycle, cancellationToken),
        "host-factorio" => await HostFactorioAsync(arguments, lifecycle, cancellationToken),
        "share-world" => await SetWorldSharingAsync(arguments, lifecycle, WorldSharingMode.Shared, cancellationToken),
        "unshare-world" => await SetWorldSharingAsync(arguments, lifecycle, WorldSharingMode.LocalOnly, cancellationToken),
        "recovery" => await ShowRecoveryAsync(recovery, cancellationToken),
        _ => PrintUsageAndReturnError()
    };
}

static async Task DiscoverAsync(
    IEnumerable<IGameAdapter> adapters,
    CancellationToken cancellationToken)
{
    Console.WriteLine("SharedWorlds discovery");
    Console.WriteLine();

    foreach (var adapter in adapters)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var installations = await adapter.DiscoverInstallationsAsync(cancellationToken);
        if (installations.Count == 0)
        {
            continue;
        }

        Console.WriteLine($"{adapter.DisplayName} [{adapter.Id}]");

        foreach (var installation in installations)
        {
            Console.WriteLine($"  Installation: {installation.RootPath} ({installation.Source})");

            var worlds = await adapter.DiscoverWorldsAsync(installation, cancellationToken);
            foreach (var world in worlds)
            {
                Console.WriteLine($"    Save: {world.DisplayName}");
            }
        }

        Console.WriteLine();
    }
}

static async Task<int> ImportFactorioAsync(
    string[] arguments,
    WorldLifecycleService lifecycle,
    CancellationToken cancellationToken)
{
    if (arguments.Length < 2)
    {
        Console.Error.WriteLine("Usage: import-factorio <save-name>");
        return ApplicationExitCodes.UsageError;
    }

    var adapter = new FactorioAdapter();
    var installation = (await adapter.DiscoverInstallationsAsync(cancellationToken)).FirstOrDefault();
    if (installation is null)
    {
        Console.Error.WriteLine("Factorio installation not found.");
        return ApplicationExitCodes.ProductFailure;
    }

    var saveName = string.Join(' ', arguments.Skip(1));
    var detected = (await adapter.DiscoverWorldsAsync(installation, cancellationToken))
        .FirstOrDefault(world =>
            string.Equals(world.DisplayName, saveName, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(world.Id, saveName, StringComparison.OrdinalIgnoreCase));

    if (detected is null)
    {
        Console.Error.WriteLine($"Factorio save '{saveName}' not found.");
        return ApplicationExitCodes.ProductFailure;
    }

    var owner = GetLocalUser();
    var world = await lifecycle.ImportAsync(
        adapter,
        installation,
        detected,
        detected.DisplayName,
        owner,
        cancellationToken);

    Console.WriteLine($"Imported '{world.Name}' as World {world.Id}.");
    Console.WriteLine("The original save was not modified.");
    Console.WriteLine("Sharing: LocalOnly (default). Nothing is shared or hosted until you explicitly enable sharing.");
    return ApplicationExitCodes.Success;
}

static async Task<int> ContinueFactorioAsync(
    string[] arguments,
    WorldLifecycleService lifecycle,
    CancellationToken cancellationToken)
{
    if (!TryParseWorldId(arguments, "continue-factorio", out var worldId))
    {
        return ApplicationExitCodes.UsageError;
    }

    var adapter = new FactorioAdapter();
    var installation = (await adapter.DiscoverInstallationsAsync(cancellationToken)).FirstOrDefault();
    if (installation is null)
    {
        Console.Error.WriteLine("Factorio installation not found.");
        return ApplicationExitCodes.ProductFailure;
    }

    var updated = await lifecycle.ContinueLocalAsync(
        worldId,
        adapter,
        installation,
        GetLocalUser(),
        cancellationToken);

    Console.WriteLine();
    Console.WriteLine($"Local session ended. World '{updated.Name}' committed as revision {updated.CurrentStateRevisionId}.");
    return ApplicationExitCodes.Success;
}

static async Task<int> HostFactorioAsync(
    string[] arguments,
    WorldLifecycleService lifecycle,
    CancellationToken cancellationToken)
{
    if (!TryParseWorldId(arguments, "host-factorio", out var worldId))
    {
        return ApplicationExitCodes.UsageError;
    }

    var adapter = new FactorioAdapter();
    var installation = (await adapter.DiscoverInstallationsAsync(cancellationToken)).FirstOrDefault();
    if (installation is null)
    {
        Console.Error.WriteLine("Factorio installation not found.");
        return ApplicationExitCodes.ProductFailure;
    }

    var updated = await lifecycle.ContinueAsHostAsync(
        worldId,
        adapter,
        installation,
        GetLocalUser(),
        cancellationToken);

    Console.WriteLine();
    Console.WriteLine($"Hosted session ended. World '{updated.Name}' committed as revision {updated.CurrentStateRevisionId}.");
    return ApplicationExitCodes.Success;
}

static async Task<int> SetWorldSharingAsync(
    string[] arguments,
    WorldLifecycleService lifecycle,
    WorldSharingMode sharingMode,
    CancellationToken cancellationToken)
{
    var command = sharingMode == WorldSharingMode.Shared ? "share-world" : "unshare-world";
    if (!TryParseWorldId(arguments, command, out var worldId))
    {
        return ApplicationExitCodes.UsageError;
    }

    var updated = await lifecycle.SetSharingModeAsync(worldId, sharingMode, cancellationToken);
    Console.WriteLine($"World '{updated.Name}' sharing mode: {updated.SharingMode}.");

    if (sharingMode == WorldSharingMode.Shared)
    {
        Console.WriteLine("This World is now explicitly eligible for Share / Host / Join workflows.");
    }
    else
    {
        Console.WriteLine("This World is local-only. Host / Join workflows are blocked.");
    }

    return ApplicationExitCodes.Success;
}

static bool TryParseWorldId(
    string[] arguments,
    string command,
    out WorldId worldId)
{
    if (arguments.Length != 2 || !Guid.TryParse(arguments[1], out var parsedWorldId))
    {
        Console.Error.WriteLine($"Usage: {command} <world-id>");
        worldId = default;
        return false;
    }

    worldId = new WorldId(parsedWorldId);
    return true;
}

static async Task<int> ShowRecoveryAsync(
    IWorkspaceRecoveryStore recoveryStore,
    CancellationToken cancellationToken)
{
    var records = await recoveryStore.ListAsync(cancellationToken);
    if (records.Count == 0)
    {
        Console.WriteLine("No prepared workspace recovery records exist.");
        return ApplicationExitCodes.Success;
    }

    Console.WriteLine("Prepared workspace recovery records");
    Console.WriteLine();

    foreach (var record in records.OrderByDescending(record => record.UpdatedAt))
    {
        cancellationToken.ThrowIfCancellationRequested();

        var status = record.Status == WorkspaceRecoveryStatus.Active
            ? "Active (possible interrupted session after restart)"
            : record.Status.ToString();

        Console.WriteLine($"- Workspace: {record.Id}");
        Console.WriteLine($"  World: {record.WorldId}");
        Console.WriteLine($"  Adapter: {record.AdapterId}");
        Console.WriteLine($"  Status: {status}");
        Console.WriteLine($"  Directory: {record.WorkingDirectory}");
        Console.WriteLine($"  Updated: {record.UpdatedAt:O}");
        if (!string.IsNullOrWhiteSpace(record.Reason))
        {
            Console.WriteLine($"  Reason: {record.Reason}");
        }

        Console.WriteLine();
    }

    return ApplicationExitCodes.Success;
}

static UserIdentity GetLocalUser()
    => new(
        Provider: "local",
        ExternalId: Environment.UserName,
        DisplayName: Environment.UserName);

static string GetLocalDataRoot()
{
    var path = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    return string.IsNullOrWhiteSpace(path) ? Path.GetTempPath() : path;
}

static int PrintUsageAndReturnError()
{
    PrintUsage();
    return ApplicationExitCodes.UsageError;
}

static void PrintUsage()
{
    Console.WriteLine("SharedWorlds");
    Console.WriteLine();
    Console.WriteLine("Commands:");
    Console.WriteLine("  discover");
    Console.WriteLine("  import-factorio <save-name>");
    Console.WriteLine("  continue-factorio <world-id>    # local/private single-player");
    Console.WriteLine("  share-world <world-id>          # explicit opt-in for Share / Host / Join");
    Console.WriteLine("  unshare-world <world-id>        # return to local-only");
    Console.WriteLine("  host-factorio <world-id>        # requires sharing enabled");
    Console.WriteLine("  recovery");
}
