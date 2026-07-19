using SharedWorlds.Core.Abstractions;
using SharedWorlds.GameAdapters.Factorio;
using SharedWorlds.GameAdapters.ProjectZomboid;
using SharedWorlds.GameAdapters.SevenDaysToDie;

IGameAdapter[] adapters =
[
    new FactorioAdapter(),
    new SevenDaysToDieAdapter(),
    new ProjectZomboidAdapter()
];

Console.WriteLine("SharedWorlds foundation");
Console.WriteLine("Steam is the platform. Games are adapters. Worlds are the product.");
Console.WriteLine();

foreach (var adapter in adapters)
{
    Console.WriteLine($"- {adapter.DisplayName} [{adapter.Id}] :: {adapter.Capabilities}");
}
