using SharedWorlds.Core.Abstractions;
using SharedWorlds.GameAdapters.Factorio;
using SharedWorlds.GameAdapters.Palworld;
using SharedWorlds.GameAdapters.ProjectZomboid;
using SharedWorlds.GameAdapters.SevenDaysToDie;
using SharedWorlds.GameAdapters.Terraria;

namespace SharedWorlds.Desktop;

internal static class DesktopGameAdapterCatalog
{
    public static IReadOnlyDictionary<string, IGameAdapter> Create()
    {
        IGameAdapter[] adapters =
        [
            new FactorioAdapter(),
            new PalworldAdapter(),
            new SevenDaysToDieAdapter(),
            new ProjectZomboidAdapter(),
            new TerrariaAdapter()
        ];

        return adapters.ToDictionary(adapter => adapter.Id, StringComparer.Ordinal);
    }
}
