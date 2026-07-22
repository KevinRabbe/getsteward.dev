using System.Windows;
using System.Windows.Controls;

namespace SharedWorlds.Desktop;

public partial class MainWindow
{
    private readonly Button _invitationsButton = new()
    {
        Content = "Invites",
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
        _invitationsButton.Click += InvitationsButton_Click;

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
            StatusText.Text = "Connect authenticated Steward to view shared World invitations.";
            return;
        }

        var dialog = new PendingInvitationsDialog(remote.Access)
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
            _invitationsButton.Content = "Invites";
            _invitationsButton.IsEnabled = false;
            _invitationsButton.ToolTip = "Connect authenticated Steward to view invitations.";
            return;
        }

        try
        {
            var invitations = await remote.Access.ListPendingInvitationsAsync(cancellationToken);
            _invitationsButton.Content = invitations.Count == 0
                ? "Invites"
                : $"Invites ({invitations.Count})";
            _invitationsButton.IsEnabled = !_isBusy;
            _invitationsButton.ToolTip = invitations.Count == 0
                ? "No pending shared World invitations."
                : $"{invitations.Count} pending shared World invitation{(invitations.Count == 1 ? string.Empty : "s")}.";
        }
        catch (Exception exception) when (IsRemoteAvailabilityFailure(exception))
        {
            _invitationsButton.Content = "Invites";
            _invitationsButton.IsEnabled = false;
            _invitationsButton.ToolTip = "Steward could not load invitations. Reconnect the shared service and try again.";
        }
    }
}
