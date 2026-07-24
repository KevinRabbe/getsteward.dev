using SharedWorlds.Core.Abstractions;
using SharedWorlds.GameAdapters.ProjectZomboid;
using SharedWorlds.GameAdapters.SevenDaysToDie;

namespace SharedWorlds.Desktop;

public partial class MainWindow
{
    private void RegisterAdditionalProductionAdapters()
    {
        if (_registeredGameAdapters is not IDictionary<string, IGameAdapter> adapters)
        {
            throw new InvalidOperationException(
                "Steward Desktop adapter registry is unexpectedly immutable during startup.");
        }

        adapters["7-days-to-die"] = new SevenDaysToDieAdapter();
        adapters["project-zomboid"] = new ProjectZomboidAdapter();
    }
}
