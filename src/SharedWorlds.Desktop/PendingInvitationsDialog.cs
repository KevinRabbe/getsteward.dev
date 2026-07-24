using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using SharedWorlds.Infrastructure.Remote;

namespace SharedWorlds.Desktop;

internal sealed class PendingInvitationsDialog : Window
{
    private readonly StewardWorldAccessClient _access;
    private readonly ListBox _invitations = new();
    private readonly Button _acceptButton = new() { Content = "Accept", Padding = new Thickness(12, 6, 12, 6) };
    private readonly Button _declineButton = new() { Content = "Decline", Padding = new Thickness(12, 6, 12, 6) };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Opacity = 0.82 };
    private bool _busy;

    public PendingInvitationsDialog(StewardWorldAccessClient access)
    {
        ArgumentNullException.ThrowIfNull(access);
        _access = access;

        Title = "Shared World invitations";
        Width = 560;
        Height = 420;
        MinWidth = 460;
        MinHeight = 340;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        UseLayoutRounding = true;
        SnapsToDevicePixels = true;
        Background = (Brush)Application.Current.FindResource("AppBackgroundBrush");
        Foreground = (Brush)Application.Current.FindResource("TextBrush");
        Content = BuildContent();

        _invitations.SelectionChanged += (_, _) => UpdateActions();
        _acceptButton.Click += AcceptButton_Click;
        _declineButton.Click += DeclineButton_Click;
        Loaded += async (_, _) => await ReloadAsync();
    }

    public bool MembershipChanged { get; private set; }

    private UIElement BuildContent()
    {
        var root = new Grid { Margin = new Thickness(20) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var heading = new StackPanel();
        heading.Children.Add(new TextBlock
        {
            Text = "Shared World invitations",
            FontSize = 22,
            FontWeight = FontWeights.SemiBold
        });
        heading.Children.Add(new TextBlock
        {
            Text = "Accepting an invitation adds this Steam account as a flat World member. It does not grant hosting priority or a special gameplay role.",
            Margin = new Thickness(0, 6, 0, 14),
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.78
        });
        Grid.SetRow(heading, 0);
        root.Children.Add(heading);

        _invitations.DisplayMemberPath = nameof(InvitationRow.DisplayText);
        _invitations.Background = (Brush)FindResource("PanelBrush");
        _invitations.Foreground = (Brush)FindResource("TextBrush");
        _invitations.BorderBrush = (Brush)FindResource("BorderBrush");
        AutomationProperties.SetName(_invitations, "Pending shared World invitations");
        Grid.SetRow(_invitations, 1);
        root.Children.Add(_invitations);

        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 12, 0, 0)
        };
        _acceptButton.Margin = new Thickness(0, 0, 8, 0);
        actions.Children.Add(_acceptButton);
        actions.Children.Add(_declineButton);
        Grid.SetRow(actions, 2);
        root.Children.Add(actions);

        var footer = new Grid { Margin = new Thickness(0, 18, 0, 0) };
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        AutomationProperties.SetLiveSetting(_status, AutomationLiveSetting.Polite);
        footer.Children.Add(_status);
        var close = new Button
        {
            Content = "Close",
            Padding = new Thickness(16, 6, 16, 6),
            Margin = new Thickness(12, 0, 0, 0)
        };
        close.Click += (_, _) => Close();
        Grid.SetColumn(close, 1);
        footer.Children.Add(close);
        Grid.SetRow(footer, 3);
        root.Children.Add(footer);

        return root;
    }

    private async void AcceptButton_Click(object sender, RoutedEventArgs e)
    {
        if (_invitations.SelectedItem is not InvitationRow row)
        {
            return;
        }

        await RunAsync(async () =>
        {
            await _access.AcceptInvitationAsync(row.Invitation.InvitationId);
            MembershipChanged = true;
            _status.Text = "Invitation accepted. The shared World is now available to this account.";
            await ReloadAsync(preserveStatus: true);
        });
    }

    private async void DeclineButton_Click(object sender, RoutedEventArgs e)
    {
        if (_invitations.SelectedItem is not InvitationRow row)
        {
            return;
        }

        await RunAsync(async () =>
        {
            await _access.DeclineInvitationAsync(row.Invitation.InvitationId);
            _status.Text = "Invitation declined.";
            await ReloadAsync(preserveStatus: true);
        });
    }

    private async Task ReloadAsync(bool preserveStatus = false)
    {
        var rows = (await _access.ListPendingInvitationsAsync())
            .OrderBy(invitation => invitation.CreatedAt)
            .Select(invitation => new InvitationRow(invitation))
            .ToArray();
        _invitations.ItemsSource = rows;
        _invitations.SelectedIndex = rows.Length == 0 ? -1 : 0;

        if (!preserveStatus)
        {
            _status.Text = rows.Length == 0
                ? "No pending invitations."
                : string.Empty;
        }

        UpdateActions();
    }

    private async Task RunAsync(Func<Task> operation)
    {
        if (_busy)
        {
            return;
        }

        _busy = true;
        UpdateActions();
        try
        {
            await operation();
        }
        catch (Exception exception)
        {
            _status.Text = exception.Message;
        }
        finally
        {
            _busy = false;
            UpdateActions();
        }
    }

    private void UpdateActions()
    {
        var selected = _invitations.SelectedItem is InvitationRow;
        _acceptButton.IsEnabled = !_busy && selected;
        _declineButton.IsEnabled = !_busy && selected;
    }

    private sealed record InvitationRow(StewardRemoteWorldInvitation Invitation)
    {
        public string DisplayText =>
            $"World {Invitation.WorldId.Value:D}  •  invited by Steam ID {Invitation.InvitedBy.ExternalId}";
    }
}
