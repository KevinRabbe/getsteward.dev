using SharedWorlds.Core.Abstractions;
using SharedWorlds.GameAdapters.ProjectZomboid;
using SharedWorlds.GameAdapters.SevenDaysToDie;

namespace SharedWorlds.Desktop;

public partial class MainWindow
{
    private void RegisterProductAdapters()
    {
        if (_registeredGameAdapters is not Dictionary<string, IGameAdapter> adapters)
        {
            throw new InvalidOperationException("The Steward adapter registry is not mutable during startup.");
        }

        adapters["7-days-to-die"] = new SevenDaysToDieAdapter();
        adapters["project-zomboid"] = new ProjectZomboidAdapter();
    }
}
