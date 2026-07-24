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

    IWorldStorage storage = new LocalWorldStorage(storageRoot);
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
        "worlds" => await ShowWorldsAsync(arguments, storage, adapters, cancellationToken),
        "world" => await ShowWorldDetailsAsync(arguments, storage, adapters, cancellationToken),
        "import-factorio" => await ImportFactorioAsync(arguments, lifecycle, cancellationToken),
        "continue-factorio" => await ContinueFactorioAsync(arguments, lifecycle, storage, cancellationToken),
        "host-factorio" => await HostFactorioAsync(arguments, lifecycle, storage, cancellationToken),
        "recovery" => await ShowRecoveryAsync(recovery, cancellationToken),
        _ => PrintUsageAndReturnError()
    };
}

static async Task DiscoverAsync(
    IEnumerable<IGameAdapter> adapters,
    CancellationToken cancellationToken)
{
    Console.WriteLine("Steward discovery");
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

static async Task<int> ShowWorldsAsync(
    string[] arguments,
    IWorldStorage storage,
    IReadOnlyList<IGameAdapter> adapters,
    CancellationToken cancellationToken)
{
    if (arguments.Length != 1)
    {
        Console.Error.WriteLine("Usage: worlds");
        return ApplicationExitCodes.UsageError;
    }

    var worlds = await storage.ListWorldsAsync(cancellationToken);
    if (worlds.Count == 0)
    {
        Console.WriteLine("No managed Worlds exist yet.");
        Console.WriteLine("Use 'discover' to find saves, then import one.");
        return ApplicationExitCodes.Success;
    }

    Console.WriteLine($"Managed Worlds ({worlds.Count})");
    Console.WriteLine();

    foreach (var world in worlds)
    {
        cancellationToken.ThrowIfCancellationRequested();

        Console.WriteLine($"- {world.Name}");
        Console.WriteLine($"  ID: {world.Id} (short: {ShortId(world.Id)})");
        Console.WriteLine($"  Game: {GetAdapterDisplayName(adapters, world.GameAdapterId)} [{world.GameAdapterId}]");
        Console.WriteLine($"  Sharing: {FormatSharingMode(world.SharingMode)}");
        Console.WriteLine($"  State: {FormatRevision(world.CurrentStateRevisionId)}");
        Console.WriteLine();
    }

    Console.WriteLine("World selectors accept a full ID, a unique World name, or a unique ID prefix of at least 8 characters.");
    Console.WriteLine("Example: world newme");
    return ApplicationExitCodes.Success;
}

static async Task<int> ShowWorldDetailsAsync(
    string[] arguments,
    IWorldStorage storage,
    IReadOnlyList<IGameAdapter> adapters,
    CancellationToken cancellationToken)
{
    if (!TryGetWorldSelector(arguments, "world", out var selector))
    {
        return ApplicationExitCodes.UsageError;
    }

    var world = await ResolveWorldAsync(selector, storage, cancellationToken);
    if (world is null)
    {
        return ApplicationExitCodes.ProductFailure;
    }

    Console.WriteLine($"World: {world.Name}");
    Console.WriteLine($"ID: {world.Id}");
    Console.WriteLine($"Game: {GetAdapterDisplayName(adapters, world.GameAdapterId)} [{world.GameAdapterId}]");
    Console.WriteLine($"Sharing: {FormatSharingMode(world.SharingMode)}");
    Console.WriteLine();

    Console.WriteLine($"Members ({world.Members.Count}):");
    foreach (var member in world.Members)
    {
        Console.WriteLine($"  - {FormatUser(member)}");
    }

    Console.WriteLine();
    Console.WriteLine("Environment:");
    if (world.CurrentEnvironmentRevisionId is { } environmentRevisionId)
    {
        var environment = await storage.LoadEnvironmentRevisionAsync(
            world.Id,
            environmentRevisionId,
            cancellationToken);

        Console.WriteLine($"  Revision: {environmentRevisionId}");
        if (environment is null)
        {
            Console.WriteLine("  Status: metadata missing");
        }
        else
        {
            Console.WriteLine($"  Game version: {environment.Manifest.GameVersion}");
            Console.WriteLine($"  Components: {environment.Manifest.Components.Count}");
            Console.WriteLine($"  Created: {environment.CreatedAt:O}");
            Console.WriteLine($"  Created by: {FormatUser(environment.CreatedBy)}");
        }
    }
    else
    {
        Console.WriteLine("  Revision: none");
    }

    Console.WriteLine();
    Console.WriteLine("State:");
    if (world.CurrentStateRevisionId is { } stateRevisionId)
    {
        var state = await storage.LoadStateRevisionAsync(
            world.Id,
            stateRevisionId,
            cancellationToken);

        Console.WriteLine($"  Revision: {stateRevisionId}");
        if (state is null)
        {
            Console.WriteLine("  Status: metadata missing");
        }
        else
        {
            Console.WriteLine($"  Parent: {FormatRevision(state.ParentRevisionId)}");
            Console.WriteLine($"  Created: {state.CreatedAt:O}");
            Console.WriteLine($"  Created by: {FormatUser(state.CreatedBy)}");
        }
    }
    else
    {
        Console.WriteLine("  Revision: none");
    }

    Console.WriteLine();
    Console.WriteLine("Available CLI actions:");
    if (world.SharingMode == WorldSharingMode.Shared)
    {
        Console.WriteLine("  None. Shared Worlds require authenticated Steward backend authority and are never writable through this local CLI.");
    }
    else if (string.Equals(world.GameAdapterId, "factorio", StringComparison.Ordinal))
    {
        Console.WriteLine($"  continue-factorio {ShortId(world.Id)}");
        Console.WriteLine($"  host-factorio {ShortId(world.Id)}");
        Console.WriteLine("  Persistent Share / Manage access is owned by the authenticated Steward product flow, not this local CLI.");
    }
    else
    {
        Console.WriteLine("  No writable CLI action is exposed for this adapter.");
    }

    return ApplicationExitCodes.Success;
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
    Console.WriteLine("Sharing: LocalOnly (default). Nothing is shared automatically; temporary hosting is a separate action.");
    Console.WriteLine($"You can now use the World name directly: continue-factorio {world.Name}");
    return ApplicationExitCodes.Success;
}

static async Task<int> ContinueFactorioAsync(
    string[] arguments,
    WorldLifecycleService lifecycle,
    IWorldStorage storage,
    CancellationToken cancellationToken)
{
    if (!TryGetWorldSelector(arguments, "continue-factorio", out var selector))
    {
        return ApplicationExitCodes.UsageError;
    }

    var world = await ResolveWorldAsync(selector, storage, cancellationToken);
    if (world is null)
    {
        return ApplicationExitCodes.ProductFailure;
    }

    if (!IsWritableThroughLocalFactorioCli(world, out var refusal))
    {
        Console.Error.WriteLine(refusal);
        return ApplicationExitCodes.ProductFailure;
    }

    var adapter = new FactorioAdapter();
    var installation = (await adapter.DiscoverInstallationsAsync(cancellationToken)).FirstOrDefault();
    if (installation is null)
    {
        Console.Error.WriteLine("Factorio installation not found.");
        return ApplicationExitCodes.ProductFailure;
    }

    var updated = await lifecycle.ContinueLocalAsync(
        world.Id,
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
    IWorldStorage storage,
    CancellationToken cancellationToken)
{
    if (!TryGetWorldSelector(arguments, "host-factorio", out var selector))
    {
        return ApplicationExitCodes.UsageError;
    }

    var world = await ResolveWorldAsync(selector, storage, cancellationToken);
    if (world is null)
    {
        return ApplicationExitCodes.ProductFailure;
    }

    if (!IsWritableThroughLocalFactorioCli(world, out var refusal))
    {
        Console.Error.WriteLine(refusal);
        return ApplicationExitCodes.ProductFailure;
    }

    var adapter = new FactorioAdapter();
    var installation = (await adapter.DiscoverInstallationsAsync(cancellationToken)).FirstOrDefault();
    if (installation is null)
    {
        Console.Error.WriteLine("Factorio installation not found.");
        return ApplicationExitCodes.ProductFailure;
    }

    var updated = await lifecycle.ContinueAsHostAsync(
        world.Id,
        adapter,
        installation,
        GetLocalUser(),
        cancellationToken);

    Console.WriteLine();
    Console.WriteLine($"Hosted session ended. World '{updated.Name}' committed as revision {updated.CurrentStateRevisionId}.");
    return ApplicationExitCodes.Success;
}

static bool IsWritableThroughLocalFactorioCli(World world, out string refusal)
{
    if (world.SharingMode == WorldSharingMode.Shared)
    {
        refusal =
            $"World '{world.Name}' is Shared. Shared Worlds require authenticated Steward backend authority and cannot use the local CLI writer.";
        return false;
    }

    if (!string.Equals(world.GameAdapterId, "factorio", StringComparison.Ordinal))
    {
        refusal =
            $"World '{world.Name}' uses adapter '{world.GameAdapterId}', not Factorio. This CLI command will not cross adapter boundaries.";
        return false;
    }

    refusal = string.Empty;
    return true;
}

static bool TryGetWorldSelector(
    string[] arguments,
    string command,
    out string selector)
{
    if (arguments.Length != 2 || string.IsNullOrWhiteSpace(arguments[1]))
    {
        Console.Error.WriteLine($"Usage: {command} <world>");
        selector = string.Empty;
        return false;
    }

    selector = arguments[1].Trim();
    return true;
}

static async Task<World?> ResolveWorldAsync(
    string selector,
    IWorldStorage storage,
    CancellationToken cancellationToken)
{
    if (Guid.TryParse(selector, out var parsedWorldId))
    {
        var exactWorld = await storage.LoadWorldAsync(new WorldId(parsedWorldId), cancellationToken);
        if (exactWorld is not null)
        {
            return exactWorld;
        }

        Console.Error.WriteLine($"World '{selector}' not found.");
        return null;
    }

    var worlds = await storage.ListWorldsAsync(cancellationToken);
    var nameMatches = worlds
        .Where(world => string.Equals(world.Name, selector, StringComparison.OrdinalIgnoreCase))
        .ToArray();

    if (nameMatches.Length == 1)
    {
        return nameMatches[0];
    }

    if (nameMatches.Length > 1)
    {
        PrintAmbiguousWorldSelector(selector, nameMatches);
        return null;
    }

    var compactSelector = selector.Replace("-", string.Empty, StringComparison.Ordinal);
    if (compactSelector.Length >= 8 && compactSelector.All(Uri.IsHexDigit))
    {
        var idMatches = worlds
            .Where(world => world.Id.ToString().StartsWith(compactSelector, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        if (idMatches.Length == 1)
        {
            return idMatches[0];
        }

        if (idMatches.Length > 1)
        {
            PrintAmbiguousWorldSelector(selector, idMatches);
            return null;
        }
    }

    Console.Error.WriteLine($"World '{selector}' not found. Run 'worlds' to list managed Worlds.");
    return null;
}

static void PrintAmbiguousWorldSelector(string selector, IReadOnlyList<World> matches)
{
    Console.Error.WriteLine($"World selector '{selector}' is ambiguous. Use one of these ID prefixes:");
    foreach (var match in matches)
    {
        Console.Error.WriteLine($"  {ShortId(match.Id)}  {match.Name}");
    }
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

static string GetAdapterDisplayName(
    IEnumerable<IGameAdapter> adapters,
    string adapterId)
    => adapters.FirstOrDefault(adapter => string.Equals(adapter.Id, adapterId, StringComparison.Ordinal))?.DisplayName
        ?? adapterId;

static string FormatSharingMode(WorldSharingMode sharingMode)
    => sharingMode == WorldSharingMode.LocalOnly ? "LocalOnly (private)" : "Shared (remote authority)";

static string FormatRevision(RevisionId? revisionId)
    => revisionId is { } value ? value.ToString() : "none";

static string ShortId(WorldId worldId)
    => worldId.ToString()[..8];

static string FormatUser(UserIdentity? user)
{
    if (user is null)
    {
        return "unknown";
    }

    var displayName = string.IsNullOrWhiteSpace(user.DisplayName)
        ? user.ExternalId
        : user.DisplayName;
    return $"{displayName} ({user.Provider}:{user.ExternalId})";
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
    Console.WriteLine("Steward");
    Console.WriteLine();
    Console.WriteLine("Commands:");
    Console.WriteLine("  discover");
    Console.WriteLine("  worlds                              # list managed Worlds");
    Console.WriteLine("  world <world>                       # show World details");
    Console.WriteLine("  import-factorio <save-name>");
    Console.WriteLine("  continue-factorio <world>           # LocalOnly writable play");
    Console.WriteLine("  host-factorio <world>               # LocalOnly temporary hosting; persistent sharing is separate");
    Console.WriteLine("  recovery");
    Console.WriteLine();
    Console.WriteLine("Shared Worlds are writable only through authenticated Steward backend authority.");
    Console.WriteLine("Persistent Share / Manage access is available only through the authenticated Steward product flow.");
    Console.WriteLine("<world> may be a full ID, a unique World name, or a unique ID prefix of at least 8 characters.");
}
