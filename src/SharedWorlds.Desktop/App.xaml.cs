using System.IO;
using System.Windows;
using System.Windows.Threading;
using SharedWorlds.Infrastructure.Diagnostics;

namespace SharedWorlds.Desktop;

public partial class App : Application
{
    protected override async void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnAppDomainUnhandledException;
        base.OnStartup(e);

        var startupPortableWorldPath = PortableWorldStartupActivation.ResolvePath(e.Args);
        if (!TryBecomePrimaryDesktop(startupPortableWorldPath))
        {
            // A primary Safe World process already owns this user/session. The activation request was
            // handed off (or failed closed); never construct a second local storage/runtime writer.
            Shutdown();
            return;
        }

        // Register only as an available per-user handler. Windows remains authoritative over which
        // application is the user's default for .safeworld.
        _ = PortableWorldFileAssociation.TryRegisterCurrentExecutable();

        var window = new MainWindow
        {
            Title = "Safe World"
        };
        MainWindow = window;

        // Start initialization before showing the window so the legacy Loaded handler is removed
        // synchronously. Show the window before awaiting asynchronous data loading; otherwise WPF
        // sees no open windows after OnStartup yields and may shut the application down immediately.
        var initialization = window.InitializeUnifiedStartupAsync();
        window.Show();
        await initialization;
        await CompletePrimaryDesktopStartupAsync(window);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        DisposeDesktopSingleInstance();
        DispatcherUnhandledException -= OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException -= OnAppDomainUnhandledException;
        base.OnExit(e);
    }

    private static void OnDispatcherUnhandledException(
        object sender,
        DispatcherUnhandledExceptionEventArgs e)
    {
        var incident = LocalDiagnosticLog.TryWriteException(e.Exception, GetDiagnosticsRoot());
        var diagnosticReference = incident.LogPath is null
            ? $"Incident ID: {incident.Id}"
            : $"Incident ID: {incident.Id}{Environment.NewLine}Diagnostic log: {incident.LogPath}";
        MessageBox.Show(
            $"Safe World encountered an unexpected failure and will stop rather than continue in an unknown state.{Environment.NewLine}{Environment.NewLine}" +
            $"{DesktopErrorMessage.Safe(e.Exception)}{Environment.NewLine}{Environment.NewLine}{diagnosticReference}",
            "Safe World unexpected failure",
            MessageBoxButton.OK,
            MessageBoxImage.Error);

        // Do not set e.Handled. Unknown dispatcher failures remain fatal by design.
    }

    private static void OnAppDomainUnhandledException(object? sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception exception)
        {
            _ = LocalDiagnosticLog.TryWriteException(exception, GetDiagnosticsRoot());
        }
    }

    private static string GetDiagnosticsRoot()
    {
        var localDataRoot = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localDataRoot))
        {
            localDataRoot = Path.GetTempPath();
        }

        return Path.Combine(localDataRoot, "SharedWorlds", "logs");
    }
}
