using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using SharedWorlds.Core.Abstractions;
using SharedWorlds.Core.Domain;
using SharedWorlds.Core.Worlds;

namespace SharedWorlds.Desktop;

public partial class MainWindow
{
    private bool _hostStopRequestInFlight;
    private bool _hostHandoffRequestInFlight;
    private Button? _handoffHostButton;

    private async void StopHostingButton_Click(object sender, RoutedEventArgs e)
    {
        var world = _selectedWorld;
        if (world is null ||
            !TryGetAdapter(world.GameAdapterId, out var adapter) ||
            !adapter.Capabilities.HasFlag(GameAdapterCapabilities.AutomaticHostStop))
        {
            return;
        }

        var responsibility = _responsibilityTracker.Current;
        if (responsibility.Kind != WorldLifecycleResponsibilityKind.ActiveLifecycle ||
            responsibility.WorldId != world.Id ||
            responsibility.Mode != ManagedWorldSessionMode.Hosted ||
            responsibility.Phase != WorldLifecyclePhase.Running ||
            _hostStopRequestInFlight ||
            _hostHandoffRequestInFlight)
        {
            return;
        }

        _hostStopRequestInFlight = true;
        UpdateManagedHostStopUi();
        StatusText.Text = $"Saving and stopping {world.Name}...";

        try
        {
            var lifecycle = GetLifecycleForWorld(world);
            if (!await lifecycle.RequestHostStopAsync(world.Id))
            {
                StatusText.Text =
                    $"{adapter.DisplayName} is still entering its hosted session. Try Stop and Save again once it is running.";
            }
        }
        catch (Exception exception)
        {
            StatusText.Text = $"Could not safely stop {world.Name}. Steward is still protecting this hosted session.";
            ShowError("Could not safely stop hosting", exception);
        }
        finally
        {
            _hostStopRequestInFlight = false;
            UpdateManagedHostStopUi();
        }
    }

    private async void HandoffHostButton_Click(object sender, RoutedEventArgs e)
    {
        var world = _selectedWorld;
        var peer = _peerRuntime;
        if (world is null ||
            peer is null ||
            !_peerWorldIds.Contains(world.Id) ||
            _hostStopRequestInFlight ||
            _hostHandoffRequestInFlight)
        {
            return;
        }

        var responsibility = _responsibilityTracker.Current;
        if (responsibility.Kind != WorldLifecycleResponsibilityKind.ActiveLifecycle ||
            responsibility.WorldId != world.Id ||
            responsibility.Mode != ManagedWorldSessionMode.Hosted ||
            responsibility.Phase != WorldLifecyclePhase.Running)
        {
            return;
        }

        try
        {
            var canonical = await peer.Storage.LoadWorldAsync(world.Id)
                ?? throw new InvalidOperationException(
                    "The canonical peer World is no longer available on this Steward installation.");
            if (canonical.PeerAuthority is not { } authority ||
                !SameStableUser(authority.Holder, peer.User))
            {
                throw new InvalidOperationException(
                    "Only the current persistent peer authority holder can hand this hosted World to another member.");
            }

            var liveMembers = await peer.Lobby.ListCurrentMembersAsync(
                canonical.Id,
                peer.User,
                authority.Generation);
            var dialog = new PeerHostHandoffDialog(
                canonical,
                peer.User,
                liveMembers)
            {
                Owner = this
            };
            if (dialog.ShowDialog() != true || dialog.SelectedHost is not { } requestedHost)
            {
                return;
            }

            _hostHandoffRequestInFlight = true;
            UpdateManagedHostStopUi();
            StatusText.Text =
                $"Handing '{world.Name}' to {FormatPeerHandoffTarget(requestedHost)}. Steward is stopping safely and will transfer the final saved World before authority moves...";

            var lifecycle = GetLifecycleForWorld(world);
            if (!await lifecycle.RequestHostHandoffAsync(
                    world.Id,
                    requestedHost))
            {
                _hostHandoffRequestInFlight = false;
                StatusText.Text =
                    "The managed host is not ready for handoff yet. Try again once the hosted session is running.";
            }
        }
        catch (Exception exception)
        {
            _hostHandoffRequestInFlight = false;
            StatusText.Text =
                $"Could not request safe host handoff for {world.Name}. The current host still retains protected authority.";
            ShowError("Could not hand off hosting", exception);
        }
        finally
        {
            UpdateManagedHostStopUi();
        }
    }

    private void UpdateManagedHostStopUi()
    {
        EnsurePeerHostHandoffButton();

        var world = _selectedWorld;
        var responsibility = _responsibilityTracker.Current;
        var adapterSupportsStop = world is not null &&
            TryGetAdapter(world.GameAdapterId, out var adapter) &&
            adapter.Capabilities.HasFlag(GameAdapterCapabilities.AutomaticHostStop);
        var canStop = adapterSupportsStop &&
            responsibility.Kind == WorldLifecycleResponsibilityKind.ActiveLifecycle &&
            responsibility.WorldId == world!.Id &&
            responsibility.Mode == ManagedWorldSessionMode.Hosted &&
            responsibility.Phase == WorldLifecyclePhase.Running;

        if (!canStop)
        {
            _hostHandoffRequestInFlight = false;
        }

        const string helpText =
            "Save the World, stop the hosted session safely, restore any temporary game runtime changes, then let Steward capture the updated World.";

        StopHostingButton.Visibility = canStop ? Visibility.Visible : Visibility.Collapsed;
        StopHostingButton.IsEnabled = canStop &&
                                      !_hostStopRequestInFlight &&
                                      !_hostHandoffRequestInFlight;
        StopHostingButton.Content = _hostStopRequestInFlight ? "Saving..." : DesktopText.StopAndSave;
        StopHostingButton.ToolTip = canStop ? helpText : null;
        AutomationProperties.SetHelpText(StopHostingButton, canStop ? helpText : string.Empty);

        if (_handoffHostButton is not null)
        {
            var canHandoff = canStop &&
                             world is not null &&
                             _peerWorldIds.Contains(world.Id) &&
                             _peerRuntime is not null;
            const string handoffHelp =
                "Choose another canonical Steam member who is already in the live lobby. Steward will stop safely, save the final World, transfer that exact revision, then move host authority.";

            _handoffHostButton.Visibility = canHandoff
                ? Visibility.Visible
                : Visibility.Collapsed;
            _handoffHostButton.IsEnabled = canHandoff &&
                                           !_hostStopRequestInFlight &&
                                           !_hostHandoffRequestInFlight;
            _handoffHostButton.Content = _hostHandoffRequestInFlight
                ? "Handing off..."
                : "Hand off host";
            _handoffHostButton.ToolTip = canHandoff ? handoffHelp : null;
            AutomationProperties.SetHelpText(
                _handoffHostButton,
                canHandoff ? handoffHelp : string.Empty);
        }
    }

    private void EnsurePeerHostHandoffButton()
    {
        if (_handoffHostButton is not null ||
            StopHostingButton.Parent is not Panel playActions)
        {
            return;
        }

        var button = new Button
        {
            Content = "Hand off host",
            Visibility = Visibility.Collapsed,
            Margin = new Thickness(0, 0, 10, 10)
        };
        AutomationProperties.SetName(button, "Hand off host");
        button.Click += HandoffHostButton_Click;

        var stopIndex = playActions.Children.IndexOf(StopHostingButton);
        playActions.Children.Insert(
            stopIndex >= 0 ? stopIndex + 1 : playActions.Children.Count,
            button);
        _handoffHostButton = button;
    }

    private static string FormatPeerHandoffTarget(UserIdentity user)
        => string.Equals(user.Provider, "steam", StringComparison.OrdinalIgnoreCase)
            ? $"Steam ID {user.ExternalId}"
            : user.ExternalId;

    private static bool SameStableUser(UserIdentity left, UserIdentity right)
        => string.Equals(left.Provider, right.Provider, StringComparison.OrdinalIgnoreCase) &&
           string.Equals(left.ExternalId, right.ExternalId, StringComparison.Ordinal);
}
