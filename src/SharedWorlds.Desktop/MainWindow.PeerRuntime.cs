using System.IO;
using System.Windows;
using SharedWorlds.Core.Abstractions;

namespace SharedWorlds.Desktop;

public partial class MainWindow
{
    private StewardDesktopPeerRuntime? _peerRuntime;
    private string? _peerRuntimeProblem;

    /// <summary>
    /// Creates Steward's embedded peer gameplay runtime from the one App-owned Steam lifetime. Remote
    /// HTTP authentication is deliberately not an input: a valid Steam AppID plus this installation's
    /// durable identity are sufficient to compose peer authority, transfer, and game-data services.
    /// </summary>
    private void InitializeStewardPeerRuntime()
    {
        DisposePeerRuntime();
        _peerRuntimeProblem = null;

        if (!StewardDesktopSteamConfiguration.TryLoad(
                out var configuration,
                out var configurationProblem))
        {
            if (configurationProblem is not null)
            {
                _peerRuntimeProblem = configurationProblem;
                StatusText.Text = $"Steam peer features are unavailable: {configurationProblem}";
            }

            return;
        }

        if (!_deviceSettingsUsableForRemote)
        {
            _peerRuntimeProblem =
                "Steward could not establish a durable installation identity on this device.";
            StatusText.Text =
                "Steam peer features are unavailable because this device has no durable Steward installation identity.";
            return;
        }

        if (Application.Current is not App app)
        {
            _peerRuntimeProblem = "The process Steam platform owner is unavailable.";
            StatusText.Text = "Steam peer features are unavailable on this launch.";
            return;
        }

        if (!app.TryGetOrCreateSteamPlatformRuntime(
                configuration!.AppId,
                out var platform,
                out var steamProblem))
        {
            _peerRuntimeProblem = steamProblem ?? "Steam platform runtime is unavailable.";
            StatusText.Text = $"Steam peer features are unavailable: {_peerRuntimeProblem}";
            return;
        }

        try
        {
            _peerRuntime = StewardDesktopPeerRuntime.Create(
                platform!,
                _deviceSettings.InstallationId,
                _localCanonicalStorage,
                _workspaceRecoveryStore,
                _localManagedSessionGate,
                CreateDesktopLifecycleObserver());
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or
            IOException or
            UnauthorizedAccessException)
        {
            _peerRuntimeProblem = exception.Message;
            StatusText.Text =
                $"Steam peer features could not start on this launch: {exception.Message}";
        }
    }

    private void DisposePeerRuntime()
    {
        Interlocked.Exchange(ref _peerRuntime, null)?.Dispose();
    }
}
