using System.Windows;

namespace SharedWorlds.Desktop;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        Activated += App_Activated;
    }

    private void App_Activated(object? sender, EventArgs e)
    {
        if (MainWindow is not MainWindow window)
        {
            return;
        }

        Activated -= App_Activated;
        window.InitializeUnifiedHostingPreference();
        window.InitializeUnifiedGameUi();
    }
}
