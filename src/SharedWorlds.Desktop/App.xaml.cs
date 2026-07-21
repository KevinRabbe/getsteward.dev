using System.Windows;

namespace SharedWorlds.Desktop;

public partial class App : Application
{
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var window = new MainWindow();
        MainWindow = window;

        // Start initialization before showing the window so the legacy Loaded handler is removed
        // synchronously. Show the window before awaiting asynchronous data loading; otherwise WPF
        // sees no open windows after OnStartup yields and may shut the application down immediately.
        var initialization = window.InitializeUnifiedStartupAsync();
        window.Show();
        await initialization;
    }
}
