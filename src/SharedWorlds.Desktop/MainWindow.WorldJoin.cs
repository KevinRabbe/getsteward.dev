using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Threading;
using SharedWorlds.Core.Abstractions;
using SharedWorlds.Core.Domain;
using SharedWorlds.Infrastructure.Remote;

namespace SharedWorlds.Desktop;

public partial class MainWindow
{
    private Button? _joinButton;
    private DispatcherTimer? _joinPresenceTimer;
    private StewardRemoteHostPresence? _selectedHostPresence;
    private Exception? _selectedHostPresenceError;
    private WorldId? _selectedHostPresenceWorldId;
    private int _hostPresenceRefreshVersion;
    private bool _joinPresenceRefreshInProgress;

    internal async Task InitializeWorldJoinUiAsync()
    {
        if (_joinButton is not null)
        {
            return;
        }

        if (HostButton.Parent is not Panel playActions)
        {
            throw new InvalidOperationException(
                "Steward could not attach Join to the common World play actions.");
        }

        var joinButton = new Button
        {
            Content = "Join",
            Margin = new Thickness(0, 0, 10, 10),
            IsEnabled = false
        };
        AutomationProperties.SetName(joinButton, "Join");
        SetJoinAvailability(joinButton, false, "Select a shared World with a ready host.");

        var hostIndex = playActions.Children.IndexOf(HostButton);
        playActions.Children.Insert(hostIndex < 0 ? playActions.Children.Count : hostIndex + 1, joinButton);
        _joinButton = joinButton;

        joinButton.Click += WorldJoinButton_Click;
        WorldList.SelectionChanged += async (_, _) => await RefreshSelectedWorldHostPresenceAsync();
        WorldList.IsEnabledChanged += (_, _) => UpdateWorldJoinActionState();

        _joinPresenceTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(10)
        };
        _joinPresenceTimer.Tick += async (_, _) => await RefreshSelectedWorldHostPresenceAsync();
        _joinPresenceTimer.Start();
        Closed += (_, _) => _joinPresenceTimer?.Stop();

        await RefreshSelectedWorldHostPresenceAsync();
    }

    private async void WorldJoinButton_Click(object sender, RoutedEventArgs e)
    {
        var world = _selectedWorld;
        var runtime = _remoteRuntime;
        var presence = _selectedHostPresenceWorldId == world?.Id
            ? _selectedHostPresence
            : null;
        if (_isBusy ||
            world is null ||
            runtime is null ||
            !_remoteWorldIds.Contains(world.Id) ||
            presence?.State != StewardRemoteHostPresenceState.Ready ||
            string.IsNullOrWhiteSpace(presence.Address) ||
            !TryGetAdapter(world.GameAdapterId, out var adapter))
        {
            return;
        }

        if (!adapter.Capabilities.HasFlag(GameAdapterCapabilities.AutomaticClientJoin))
        {
            StatusText.Text = $"{adapter.DisplayName} does not support Steward-managed Join yet.";
            return;
        }

        await RunUnifiedOperationAsync(
            $"Joining {world.Name}...",
            async () =>
            {
                var installation = await GetReadyInstallationForWorldAsync(world, adapter);
                await runtime.Join.JoinAsync(
                    world.Id,
                    adapter,
                    installation,
                    new HostConnection(
                        presence.Address,
                        presence.Port,
                        presence.JoinToken));
                StatusText.Text = $"Left hosted World '{world.Name}'.";
            });

        await RefreshSelectedWorldHostPresenceAsync();
    }

    private async Task RefreshSelectedWorldHostPresenceAsync()
    {
        if (_joinButton is null || _joinPresenceRefreshInProgress)
        {
            return;
        }

        var world = _selectedWorld;
        var runtime = _remoteRuntime;
        var refreshVersion = ++_hostPresenceRefreshVersion;
        _selectedHostPresence = null;
        _selectedHostPresenceError = null;
        _selectedHostPresenceWorldId = world?.Id;
        UpdateWorldJoinActionState();

        if (world is null ||
            runtime is null ||
            !_remoteWorldIds.Contains(world.Id) ||
            !TryGetAdapter(world.GameAdapterId, out var adapter) ||
            !adapter.Capabilities.HasFlag(GameAdapterCapabilities.AutomaticClientJoin))
        {
            return;
        }

        _joinPresenceRefreshInProgress = true;
        try
        {
            StewardRemoteHostPresence? presence = null;
            Exception? failure = null;
            try
            {
                presence = await runtime.GetHostPresenceAsync(world.Id);
            }
            catch (Exception exception) when (IsRemoteAvailabilityFailure(exception))
            {
                failure = exception;
            }

            if (refreshVersion != _hostPresenceRefreshVersion || _selectedWorld?.Id != world.Id)
            {
                return;
            }

            _selectedHostPresence = presence;
            _selectedHostPresenceError = failure;
            _selectedHostPresenceWorldId = world.Id;
        }
        finally
        {
            _joinPresenceRefreshInProgress = false;
            UpdateWorldJoinActionState();
        }
    }

    private void UpdateWorldJoinActionState()
    {
        var button = _joinButton;
        if (button is null)
        {
            return;
        }

        var world = _selectedWorld;
        if (world is null)
        {
            SetJoinAvailability(button, false, "Select a World.");
            return;
        }

        if (!_remoteWorldIds.Contains(world.Id))
        {
            SetJoinAvailability(
                button,
                false,
                "Join becomes available for shared Worlds when another device is hosting.");
            return;
        }

        if (!TryGetAdapter(world.GameAdapterId, out var adapter) ||
            !adapter.Capabilities.HasFlag(GameAdapterCapabilities.AutomaticClientJoin))
        {
            SetJoinAvailability(
                button,
                false,
                $"{adapter?.DisplayName ?? world.GameAdapterId} does not expose a validated automatic Join path yet.");
            return;
        }

        if (_remoteRuntime is null)
        {
            SetJoinAvailability(
                button,
                false,
                "Reconnect authenticated Steward before joining this shared World.");
            return;
        }

        if (!IsSelectedWorldEnvironmentReadyForPlay())
        {
            SetJoinAvailability(
                button,
                false,
                "Verify the exact World environment on this device before Join.");
            return;
        }

        if (_selectedHostPresenceError is not null)
        {
            SetJoinAvailability(button, false, "Steward could not verify host readiness right now.");
            return;
        }

        var presence = _selectedHostPresenceWorldId == world.Id
            ? _selectedHostPresence
            : null;
        if (presence is null)
        {
            SetJoinAvailability(button, false, "No ready host is currently advertised for this World.");
            return;
        }

        if (presence.State == StewardRemoteHostPresenceState.Starting)
        {
            SetJoinAvailability(button, false, "Host is starting.");
            return;
        }

        if (string.IsNullOrWhiteSpace(presence.Address))
        {
            SetJoinAvailability(button, false, "The host is not advertising a usable connection yet.");
            return;
        }

        SetJoinAvailability(
            button,
            !_isBusy,
            _isBusy
                ? "Another Steward operation is in progress."
                : $"Join the active {adapter.DisplayName} host without acquiring writable World authority.");
    }

    private static void SetJoinAvailability(Button button, bool isEnabled, string helpText)
    {
        button.IsEnabled = isEnabled;
        button.ToolTip = helpText;
        AutomationProperties.SetHelpText(button, helpText);
    }
}
