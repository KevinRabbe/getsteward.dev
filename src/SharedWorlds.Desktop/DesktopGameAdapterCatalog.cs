using SharedWorlds.Core.Abstractions;
using SharedWorlds.GameAdapters.Factorio;
using SharedWorlds.GameAdapters.Palworld;
using SharedWorlds.GameAdapters.ProjectZomboid;
using SharedWorlds.GameAdapters.SevenDaysToDie;

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
            new ProjectZomboidAdapter()
        ];

        return adapters.ToDictionary(adapter => adapter.Id, StringComparer.Ordinal);
    }
}
