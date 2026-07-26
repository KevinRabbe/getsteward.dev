using SharedWorlds.Core.Abstractions;
using SharedWorlds.GameAdapters.AbioticFactor;
using SharedWorlds.GameAdapters.Astroneer;
using SharedWorlds.GameAdapters.ConanExilesEnhanced;
using SharedWorlds.GameAdapters.CoreKeeper;
using SharedWorlds.GameAdapters.Enshrouded;
using SharedWorlds.GameAdapters.Factorio;
using SharedWorlds.GameAdapters.Icarus;
using SharedWorlds.GameAdapters.Necesse;
using SharedWorlds.GameAdapters.Palworld;
using SharedWorlds.GameAdapters.PlanetCrafter;
using SharedWorlds.GameAdapters.ProjectZomboid;
using SharedWorlds.GameAdapters.Raft;
using SharedWorlds.GameAdapters.Satisfactory;
using SharedWorlds.GameAdapters.SevenDaysToDie;
using SharedWorlds.GameAdapters.Smalland;
using SharedWorlds.GameAdapters.StardewValley;
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
            new TerrariaAdapter(),
            new StardewValleyAdapter(),
            new NecesseAdapter(),
            new CoreKeeperAdapter(),
            new PlanetCrafterAdapter(),
            new SatisfactoryAdapter(),
            new AstroneerAdapter(),
            new EnshroudedAdapter(),
            new ConanExilesEnhancedAdapter(),
            new RaftAdapter(),
            new IcarusAdapter(),
            new SmallandAdapter(),
            new AbioticFactorAdapter()
        ];

        return adapters.ToDictionary(adapter => adapter.Id, StringComparer.Ordinal);
    }
}
