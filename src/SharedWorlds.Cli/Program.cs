using SharedWorlds.Core.Abstractions;
using SharedWorlds.Core.Domain;
using SharedWorlds.Core.Worlds;
using SharedWorlds.GameAdapters.Factorio;
using SharedWorlds.GameAdapters.ProjectZomboid;
using SharedWorlds.GameAdapters.SevenDaysToDie;
using SharedWorlds.Infrastructure.Storage;

IGameAdapter[] adapters =
[
    new FactorioAdapter(),
    new SevenDaysToDieAdapter(),
    new ProjectZomboidAdapter()
];

var storageRoot = Path.Combine(
    GetLocalDataRoot(),
    "SharedWorlds",
    "data");

var storage = new LocalWorldStorage(storageRoot);
var lifecycle = new WorldLifecycleService(storage);

if (args.Length == 0 || string.Equals(args[0], "discover", StringComparison.OrdinalIgnoreCase))
{
    await DiscoverAsync(adapters);
    return;
}

switch (args[0].ToLowerInvariant())
{
    case "import-factorio":
        await ImportFactorioAsync(args, lifecycle);
        break;

    case "continue-factorio":
        await ContinueFactorioAsync(args, lifecycle);
        break;

    default:
        PrintUsage();
        break;
}

static async Task DiscoverAsync(IEnumerable<IGameAdapter> adapters)
{
    Console.WriteLine("SharedWorlds discovery");
    Console.WriteLine();

    foreach (var adapter in adapters)
    {
        var installations = await adapter.DiscoverInstallationsAsync();
        if (installations.Count == 0)
        {
            continue;
        }

        Console.WriteLine($"{adapter.DisplayName} [{adapter.Id}]");

        foreach (var installation in installations)
        {
            Console.WriteLine($"  Installation: {installation.RootPath} ({installation.Source})");

            var worlds = await adapter.DiscoverWorldsAsync(installation);
            foreach (var world in worlds)
            {
                Console.WriteLine($"    Save: {world.DisplayName}");
            }
        }

        Console.WriteLine();
    }
}

static async Task ImportFactorioAsync(
    string[] arguments,
    WorldLifecycleService lifecycle)
{
    if (arguments.Length < 2)
    {
        Console.WriteLine("Usage: import-factorio <save-name>");
        return;
    }

    var adapter = new FactorioAdapter();
    var installation = (await adapter.DiscoverInstallationsAsync()).FirstOrDefault();
    if (installation is null)
    {
        Console.WriteLine("Factorio installation not found.");
        return;
    }

    var saveName = string.Join(' ', arguments.Skip(1));
    var detected = (await adapter.DiscoverWorldsAsync(installation))
        .FirstOrDefault(world =>
            string.Equals(world.DisplayName, saveName, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(world.Id, saveName, StringComparison.OrdinalIgnoreCase));

    if (detected is null)
    {
        Console.WriteLine($"Factorio save '{saveName}' not found.");
        return;
    }

    var owner = GetLocalUser();
    var world = await lifecycle.ImportAsync(
        adapter,
        installation,
        detected,
        detected.DisplayName,
        owner);

    Console.WriteLine($"Imported '{world.Name}' as World {world.Id}.");
    Console.WriteLine("The original save was not modified.");
}

static async Task ContinueFactorioAsync(
    string[] arguments,
    WorldLifecycleService lifecycle)
{
    if (arguments.Length != 2 || !Guid.TryParse(arguments[1], out var parsedWorldId))
    {
        Console.WriteLine("Usage: continue-factorio <world-id>");
        return;
    }

    var adapter = new FactorioAdapter();
    var installation = (await adapter.DiscoverInstallationsAsync()).FirstOrDefault();
    if (installation is null)
    {
        Console.WriteLine("Factorio installation not found.");
        return;
    }

    var updated = await lifecycle.ContinueAsHostAsync(
        new WorldId(parsedWorldId),
        adapter,
        installation,
        GetLocalUser());

    Console.WriteLine();
    Console.WriteLine($"Session ended. World '{updated.Name}' committed as revision {updated.CurrentStateRevisionId}.");
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

static void PrintUsage()
{
    Console.WriteLine("SharedWorlds");
    Console.WriteLine();
    Console.WriteLine("Commands:");
    Console.WriteLine("  discover");
    Console.WriteLine("  import-factorio <save-name>");
    Console.WriteLine("  continue-factorio <world-id>");
}
