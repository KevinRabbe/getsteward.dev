using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;

namespace SharedWorlds.Desktop;

public partial class MainWindow
{
    private readonly Button _invitationsButton = new()
    {
        Content = DesktopText.Invites,
        Padding = new Thickness(12, 6, 12, 6),
        MinHeight = 32,
        Margin = new Thickness(0, 0, 8, 0)
    };
    private bool _worldInvitationsUiInitialized;

    internal async Task InitializeWorldInvitationsUiAsync()
    {
        if (_worldInvitationsUiInitialized)
        {
            return;
        }

        _worldInvitationsUiInitialized = true;
        AutomationProperties.SetName(_invitationsButton, DesktopText.SharedWorldInvitations);
        _invitationsButton.Click += InvitationsButton_Click;

        // Initial attachment keeps one ownership path for the existing control. The polished product
        // shell re-homes this same button into the global Lobby after all shell surfaces are initialized.
        if (RefreshButton.Parent is Grid header)
        {
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(_invitationsButton, 2);
            Grid.SetColumn(RefreshButton, 3);
            header.Children.Add(_invitationsButton);
        }

        await RefreshPendingInvitationCountAsync();
    }

    private async void InvitationsButton_Click(object sender, RoutedEventArgs e)
    {
        var remote = _remoteRuntime;
        if (remote is null)
        {
            StatusText.Text = "Connect Safe World to view shared World invitations.";
            return;
        }

        var dialog = new PendingInvitationsDialog(remote.Access, remote.User)
        {
            Owner = this
        };
        dialog.ShowDialog();

        if (dialog.MembershipChanged)
        {
            await RefreshUnifiedWorldsAsync(preserveStatus: true);
            StatusText.Text = "Shared World membership updated.";
        }

        await RefreshPendingInvitationCountAsync();
        if (_globalLobbyVisible)
        {
            await RefreshGlobalLobbyAsync();
        }
    }

    private async Task RefreshPendingInvitationCountAsync(
        CancellationToken cancellationToken = default)
    {
        if (!_worldInvitationsUiInitialized)
        {
            return;
        }

        var remote = _remoteRuntime;
        if (remote is null)
        {
            SetInvitationsActionState(
                DesktopText.Invites,
                false,
                "Connect Safe World to view invitations.");
            return;
        }

        try
        {
            var invitations = await remote.Access.ListPendingInvitationsAsync(cancellationToken);
            var content = invitations.Count == 0
                ? DesktopText.Invites
                : $"{DesktopText.Invites} ({invitations.Count})";
            var helpText = invitations.Count == 0
                ? "No pending shared World invitations."
                : $"{invitations.Count} pending shared World invitation{(invitations.Count == 1 ? string.Empty : "s")}.";
            SetInvitationsActionState(content, !_isBusy, helpText);
        }
        catch (Exception exception) when (IsRemoteAvailabilityFailure(exception))
        {
            SetInvitationsActionState(
                DesktopText.Invites,
                false,
                "Safe World could not load invitations. Reconnect and try again.");
        }
    }

    private void SetInvitationsActionState(string content, bool isEnabled, string helpText)
    {
        _invitationsButton.Content = content;
        _invitationsButton.IsEnabled = isEnabled;
        _invitationsButton.ToolTip = helpText;
        AutomationProperties.SetHelpText(_invitationsButton, helpText);
    }
}
