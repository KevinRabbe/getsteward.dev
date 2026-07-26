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
    private TextBlock? _joinReadinessText;
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

        if (HostButton.Parent is not Panel playActions ||
            playActions.Parent is not Panel playSection)
        {
            throw new InvalidOperationException(
                "Steward could not attach Join to the common World play actions.");
        }

        var joinButton = new Button
        {
            Content = DesktopText.Join,
            Margin = new Thickness(0, 0, 10, 10),
            IsEnabled = false
        };
        AutomationProperties.SetName(joinButton, DesktopText.Join);

        var joinReadinessText = new TextBlock
        {
            Margin = new Thickness(0, 2, 0, 0),
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Visibility = Visibility.Collapsed
        };
        joinReadinessText.SetResourceReference(TextBlock.ForegroundProperty, "MutedTextBrush");
        AutomationProperties.SetName(joinReadinessText, "Join status");
        AutomationProperties.SetLiveSetting(joinReadinessText, AutomationLiveSetting.Polite);
        RegisterLiveRegion(joinReadinessText);

        var hostIndex = playActions.Children.IndexOf(HostButton);
        playActions.Children.Insert(hostIndex < 0 ? playActions.Children.Count : hostIndex + 1, joinButton);
        var playActionsIndex = playSection.Children.IndexOf(playActions);
        playSection.Children.Insert(
            playActionsIndex < 0 ? playSection.Children.Count : playActionsIndex + 1,
            joinReadinessText);
        _joinButton = joinButton;
        _joinReadinessText = joinReadinessText;

        SetJoinAvailability(joinButton, false, "Select a shared World to check Join readiness.");
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
        var displayedPresence = _selectedHostPresenceWorldId == world?.Id
            ? _selectedHostPresence
            : null;
        if (_isBusy ||
            world is null ||
            runtime is null ||
            !_remoteWorldIds.Contains(world.Id) ||
            displayedPresence?.State != StewardRemoteHostPresenceState.Ready ||
            string.IsNullOrWhiteSpace(displayedPresence.Address) ||
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
                // Local environment selection may take time. Re-read host presence only after that
                // work, immediately before launch, so the 10-second presentation cache can never be
                // treated as current multiplayer authority after a reservation reclaim.
                var installation = await GetReadyInstallationForWorldAsync(world, adapter);
                var presence = await runtime.GetHostPresenceAsync(world.Id);
                _selectedHostPresence = presence;
                _selectedHostPresenceError = null;
                _selectedHostPresenceWorldId = world.Id;

                if (presence?.State != StewardRemoteHostPresenceState.Ready ||
                    string.IsNullOrWhiteSpace(presence.Address))
                {
                    throw new InvalidOperationException(
                        "The host is no longer ready to join. Steward did not launch the client against stale host information.");
                }

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
            !SupportsJoinPresentation(adapter))
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
            SetJoinAvailability(button, false, "Select a shared World to check Join readiness.");
            return;
        }

        if (!_remoteWorldIds.Contains(world.Id))
        {
            SetJoinAvailability(
                button,
                false,
                world.SharingMode == WorldSharingMode.Shared
                    ? "Reconnect Steward to check whether this shared World has a ready host."
                    : "Join is available for shared Worlds when another device is hosting.");
            return;
        }

        if (!TryGetAdapter(world.GameAdapterId, out var adapter) ||
            !SupportsJoinPresentation(adapter))
        {
            SetJoinAvailability(
                button,
                false,
                $"{adapter?.DisplayName ?? world.GameAdapterId} does not support one-click or native direct-connect Join yet.");
            return;
        }

        if (_remoteRuntime is null)
        {
            SetJoinAvailability(
                button,
                false,
                "Reconnect Steward to check whether a host is ready.");
            return;
        }

        if (!IsSelectedWorldEnvironmentReadyForPlay())
        {
            SetJoinAvailability(
                button,
                false,
                "Verify this World's exact environment before joining.");
            return;
        }

        if (_selectedHostPresenceError is not null)
        {
            SetJoinAvailability(button, false, "Steward cannot check host readiness right now.");
            return;
        }

        var presence = _selectedHostPresenceWorldId == world.Id
            ? _selectedHostPresence
            : null;
        if (presence is null)
        {
            SetJoinAvailability(button, false, "No one is hosting this World right now.");
            return;
        }

        if (presence.State == StewardRemoteHostPresenceState.Starting)
        {
            SetJoinAvailability(
                button,
                false,
                "A host is starting. Join will become available when it is ready.");
            return;
        }

        if (string.IsNullOrWhiteSpace(presence.Address))
        {
            SetJoinAvailability(button, false, "The host is not ready to accept connections yet.");
            return;
        }

        if (!adapter.Capabilities.HasFlag(GameAdapterCapabilities.AutomaticClientJoin) &&
            adapter is IManualDirectConnectProvider manualDirectConnect)
        {
            try
            {
                var guidance = manualDirectConnect.GetManualDirectConnectInstruction(
                    new HostConnection(
                        presence.Address,
                        presence.Port,
                        presence.JoinToken));
                SetJoinAvailability(button, false, guidance.Instruction);
            }
            catch (InvalidOperationException)
            {
                SetJoinAvailability(
                    button,
                    false,
                    "The host is ready, but it did not publish a complete native direct-connect endpoint.");
            }

            return;
        }

        SetJoinAvailability(
            button,
            !_isBusy,
            _isBusy
                ? "Another Steward operation is in progress."
                : "A host is ready. You can join now.");
    }

    private static bool SupportsJoinPresentation(IGameAdapter adapter)
    {
        ArgumentNullException.ThrowIfNull(adapter);
        return adapter.Capabilities.HasFlag(GameAdapterCapabilities.AutomaticClientJoin) ||
               adapter is IManualDirectConnectProvider;
    }

    private void SetJoinAvailability(Button button, bool isEnabled, string helpText)
    {
        button.IsEnabled = isEnabled;
        button.ToolTip = helpText;
        AutomationProperties.SetHelpText(button, helpText);

        var status = _joinReadinessText;
        var world = _selectedWorld;
        if (status is null ||
            world is null ||
            world.SharingMode != WorldSharingMode.Shared)
        {
            if (status is not null)
            {
                status.Text = string.Empty;
                status.Visibility = Visibility.Collapsed;
            }

            return;
        }

        status.Text = helpText;
        status.Visibility = Visibility.Visible;
    }
}
