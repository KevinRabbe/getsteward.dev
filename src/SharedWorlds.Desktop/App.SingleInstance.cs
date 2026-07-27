using System.Collections.Concurrent;
using System.Windows;

namespace SharedWorlds.Desktop;

public partial class App
{
    private readonly ConcurrentQueue<DesktopActivationRequest> _pendingDesktopActivations = new();
    private DesktopSingleInstanceCoordinator? _desktopSingleInstance;
    private MainWindow? _desktopWindow;
    private bool _desktopStartupReady;
    private bool _processingDesktopActivations;

    private bool TryBecomePrimaryDesktop(string? startupPortableWorldPath)
    {
        var coordinator = DesktopSingleInstanceCoordinator.CreateForCurrentSession();
        if (!coordinator.IsPrimary)
        {
            _ = coordinator.TryForward(new DesktopActivationRequest(startupPortableWorldPath));
            coordinator.Dispose();
            return false;
        }

        _desktopSingleInstance = coordinator;
        coordinator.StartListening(QueueSecondaryDesktopActivationAsync);
        if (startupPortableWorldPath is not null)
        {
            _pendingDesktopActivations.Enqueue(new DesktopActivationRequest(startupPortableWorldPath));
        }

        return true;
    }

    private Task QueueSecondaryDesktopActivationAsync(DesktopActivationRequest request)
    {
        _pendingDesktopActivations.Enqueue(request);
        _ = Dispatcher.InvokeAsync(() => _ = ProcessPendingDesktopActivationsAsync());
        return Task.CompletedTask;
    }

    private async Task CompletePrimaryDesktopStartupAsync(MainWindow window)
    {
        ArgumentNullException.ThrowIfNull(window);
        _desktopWindow = window;
        _desktopStartupReady = true;
        await ProcessPendingDesktopActivationsAsync();
    }

    private async Task ProcessPendingDesktopActivationsAsync()
    {
        if (!_desktopStartupReady || _processingDesktopActivations || _desktopWindow is null)
        {
            return;
        }

        _processingDesktopActivations = true;
        try
        {
            while (_pendingDesktopActivations.TryDequeue(out var request))
            {
                BringDesktopToFront(_desktopWindow);
                if (request.PortableWorldPath is not null)
                {
                    await _desktopWindow.OpenPortableWorldFromPathAsync(request.PortableWorldPath);
                }
            }
        }
        finally
        {
            _processingDesktopActivations = false;
        }
    }

    private static void BringDesktopToFront(Window window)
    {
        if (!window.IsVisible)
        {
            window.Show();
        }

        if (window.WindowState == WindowState.Minimized)
        {
            window.WindowState = WindowState.Normal;
        }

        _ = window.Activate();
    }

    private void DisposeDesktopSingleInstance()
    {
        _desktopStartupReady = false;
        _desktopSingleInstance?.Dispose();
        _desktopSingleInstance = null;
    }
}
