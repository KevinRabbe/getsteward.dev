using SharedWorlds.GameAdapters.Palworld;

var adapter = new PalworldAdapter();
var installations = await adapter.DiscoverInstallationsAsync();

Console.WriteLine("SharedWorlds Palworld probe (read-only)");
Console.WriteLine();

if (installations.Count == 0)
{
    Console.WriteLine("No Palworld Steam installation was detected.");
    return;
}

foreach (var installation in installations)
{
    Console.WriteLine($"Client installation: {installation.RootPath}");
    Console.WriteLine($"Source: {installation.Source}");

    if (installation.Metadata is not null)
    {
        foreach (var entry in installation.Metadata.OrderBy(entry => entry.Key, StringComparer.Ordinal))
        {
            Console.WriteLine($"  {entry.Key}: {entry.Value}");
        }
    }

    var worlds = await adapter.DiscoverWorldsAsync(installation);
    Console.WriteLine($"Detected save Worlds: {worlds.Count}");

    foreach (var world in worlds)
    {
        Console.WriteLine($"  - {world.DisplayName}");
        Console.WriteLine($"    ID: {world.Id}");
        Console.WriteLine($"    Path: {world.SourcePath}");
    }

    Console.WriteLine();
}

Console.WriteLine("Probe complete. No files were modified.");
