using System.Windows;

namespace SharedWorlds.Desktop;

public partial class App : Application
{
    protected override async void OnStartup(StartupEventArgs e)
    {
        // MainWindow is created explicitly so its legacy Factorio-only Loaded handler can be
        // removed before the window becomes visible. StartupUri would otherwise start both the
        // legacy and unified refresh pipelines concurrently.
        StartupUri = null;
        base.OnStartup(e);

        var window = new MainWindow();
        MainWindow = window;
        await window.InitializeUnifiedStartupAsync();
        window.Show();
    }
}
