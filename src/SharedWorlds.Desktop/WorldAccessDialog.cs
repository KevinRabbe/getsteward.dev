using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using SharedWorlds.Core.Domain;
using SharedWorlds.Infrastructure.Remote;

namespace SharedWorlds.Desktop;

internal sealed class WorldAccessDialog : Window
{
    private readonly StewardWorldAccessClient _access;
    private readonly World _world;
    private readonly UserIdentity _currentUser;
    private StewardRemoteIdentity _accessManager;
    private readonly ListBox _members = new();
    private readonly TextBox _inviteSteamId = new();
    private readonly Button _inviteButton = new() { Content = DesktopText.Invite, Padding = new Thickness(12, 6, 12, 6) };
    private readonly Button _revokeButton = new() { Content = DesktopText.RemoveAccess, Padding = new Thickness(12, 6, 12, 6) };
    private readonly Button _transferButton = new() { Content = DesktopText.MakeAccessManager, Padding = new Thickness(12, 6, 12, 6) };
    private readonly Button _leaveButton = new() { Content = DesktopText.LeaveWorld, Padding = new Thickness(12, 6, 12, 6) };
    private readonly TextBlock _summary = new() { TextWrapping = TextWrapping.Wrap, Opacity = 0.78 };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Opacity = 0.82 };
    private bool _busy;

    public WorldAccessDialog(
        StewardWorldAccessClient access,
        World world,
        UserIdentity currentUser,
        StewardRemoteIdentity accessManager)
    {
        ArgumentNullException.ThrowIfNull(access);
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(currentUser);
        ArgumentNullException.ThrowIfNull(accessManager);

        _access = access;
        _world = world;
        _currentUser = currentUser;
        _accessManager = accessManager;

        Title = $"{DesktopText.ManageAccess} — {world.Name}";
        Width = 560;
        Height = 500;
        MinWidth = 480;
        MinHeight = 420;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        UseLayoutRounding = true;
        SnapsToDevicePixels = true;
        Background = (Brush)Application.Current.FindResource("AppBackgroundBrush");
        Foreground = (Brush)Application.Current.FindResource("TextBrush");
        Content = BuildContent();

        _members.SelectionChanged += (_, _) => UpdateActions();
        _inviteButton.Click += InviteButton_Click;
        _revokeButton.Click += RevokeButton_Click;
        _transferButton.Click += TransferButton_Click;
        _leaveButton.Click += LeaveButton_Click;
        Loaded += async (_, _) => await ReloadAsync();
    }

    public bool WorldLeft { get; private set; }

    private UIElement BuildContent()
    {
        var root = new Grid { Margin = new Thickness(20) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var heading = new StackPanel();
        heading.Children.Add(new TextBlock
        {
            Text = _world.Name,
            FontSize = 22,
            FontWeight = FontWeights.SemiBold
        });
        _summary.Margin = new Thickness(0, 6, 0, 0);
        heading.Children.Add(_summary);
        Grid.SetRow(heading, 0);
        root.Children.Add(heading);

        var inviteRow = new Grid { Margin = new Thickness(0, 18, 0, 12) };
        inviteRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        inviteRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _inviteSteamId.MinHeight = 32;
        _inviteSteamId.VerticalContentAlignment = VerticalAlignment.Center;
        _inviteSteamId.ToolTip = "Steam ID64 of the player to invite";
        AutomationProperties.SetName(_inviteSteamId, "Steam ID64 to invite");
        AutomationProperties.SetHelpText(_inviteSteamId, "Enter the numeric Steam ID64 of the player to invite to this World.");
        inviteRow.Children.Add(_inviteSteamId);
        _inviteButton.Margin = new Thickness(10, 0, 0, 0);
        Grid.SetColumn(_inviteButton, 1);
        inviteRow.Children.Add(_inviteButton);
        Grid.SetRow(inviteRow, 1);
        root.Children.Add(inviteRow);

        _members.DisplayMemberPath = nameof(MemberRow.DisplayText);
        _members.Background = (Brush)FindResource("PanelBrush");
        _members.Foreground = (Brush)FindResource("TextBrush");
        _members.BorderBrush = (Brush)FindResource("BorderBrush");
        AutomationProperties.SetName(_members, "People with access");
        var memberItemStyle = new Style(typeof(ListBoxItem));
        memberItemStyle.Setters.Add(new Setter(
            AutomationProperties.NameProperty,
            new Binding(nameof(MemberRow.DisplayText))));
        memberItemStyle.Setters.Add(new Setter(
            FrameworkElement.FocusVisualStyleProperty,
            FindResource("StewardFocusVisualStyle")));
        _members.ItemContainerStyle = memberItemStyle;
        Grid.SetRow(_members, 2);
        root.Children.Add(_members);

        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 12, 0, 0)
        };
        _revokeButton.Margin = new Thickness(0, 0, 8, 0);
        _transferButton.Margin = new Thickness(0, 0, 8, 0);
        actions.Children.Add(_revokeButton);
        actions.Children.Add(_transferButton);
        actions.Children.Add(_leaveButton);
        Grid.SetRow(actions, 3);
        root.Children.Add(actions);

        var footer = new Grid { Margin = new Thickness(0, 18, 0, 0) };
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        AutomationProperties.SetLiveSetting(_status, AutomationLiveSetting.Polite);
        footer.Children.Add(_status);
        var close = new Button
        {
            Content = DesktopText.Close,
            Padding = new Thickness(16, 6, 16, 6),
            Margin = new Thickness(12, 0, 0, 0)
        };
        close.Click += (_, _) => Close();
        Grid.SetColumn(close, 1);
        footer.Children.Add(close);
        Grid.SetRow(footer, 4);
        root.Children.Add(footer);

        return root;
    }

    private async void InviteButton_Click(object sender, RoutedEventArgs e)
    {
        var value = _inviteSteamId.Text.Trim();
        if (!ulong.TryParse(value, out var steamId) || steamId == 0)
        {
            SetStatus("Enter the player's numeric Steam ID64.");
            return;
        }

        await RunAsync(async () =>
        {
            await _access.InviteAsync(_world.Id, "steam", value);
            _inviteSteamId.Clear();
            SetStatus($"Invitation sent to Steam ID {value}.");
            await ReloadAsync(preserveStatus: true);
        });
    }

    private async void RevokeButton_Click(object sender, RoutedEventArgs e)
    {
        if (_members.SelectedItem is not MemberRow row || row.IsAccessManager)
        {
            return;
        }

        var confirmation = MessageBox.Show(
            this,
            $"Remove access for {row.Member.Identity.ExternalId}?",
            "Remove World access?",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);
        if (confirmation != MessageBoxResult.Yes)
        {
            return;
        }

        await RunAsync(async () =>
        {
            var result = await _access.RevokeMemberAsync(
                _world.Id,
                row.Member.Identity.Provider,
                row.Member.Identity.ExternalId);
            SetStatus(result == RemoteMemberRevocationStatus.Revoked
                ? "Access removed."
                : "Access will be removed after the player's current writable responsibility resolves.");
            await ReloadAsync(preserveStatus: true);
        });
    }

    private async void TransferButton_Click(object sender, RoutedEventArgs e)
    {
        if (_members.SelectedItem is not MemberRow row || row.IsAccessManager)
        {
            return;
        }

        var confirmation = MessageBox.Show(
            this,
            $"Make {row.Member.Identity.ExternalId} the Access Manager for '{_world.Name}'?",
            "Transfer Access Manager?",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);
        if (confirmation != MessageBoxResult.Yes)
        {
            return;
        }

        await RunAsync(async () =>
        {
            await _access.TransferAccessManagerAsync(
                _world.Id,
                row.Member.Identity.Provider,
                row.Member.Identity.ExternalId);
            _accessManager = row.Member.Identity;
            SetStatus("Access Manager transferred.");
            await ReloadAsync(preserveStatus: true);
        });
    }

    private async void LeaveButton_Click(object sender, RoutedEventArgs e)
    {
        var confirmation = MessageBox.Show(
            this,
            $"Leave shared World '{_world.Name}'?",
            "Leave World?",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);
        if (confirmation != MessageBoxResult.Yes)
        {
            return;
        }

        await RunAsync(async () =>
        {
            await _access.LeaveWorldAsync(_world.Id);
            WorldLeft = true;
            DialogResult = true;
        });
    }

    private async Task ReloadAsync(bool preserveStatus = false)
    {
        var rows = (await _access.ListMembersAsync(_world.Id))
            .Select(member => new MemberRow(
                member,
                IdentityEquals(member.Identity, _accessManager)))
            .OrderByDescending(row => row.IsAccessManager)
            .ThenBy(row => row.Member.Identity.ExternalId, StringComparer.Ordinal)
            .ToArray();
        _members.ItemsSource = rows;

        var isManager = CurrentUserIsAccessManager();
        _summary.Text = isManager
            ? "You are the Access Manager. Sharing stays flat: members can continue the World; there are no gameplay roles or priority tiers."
            : $"Access Manager: Steam ID {_accessManager.ExternalId}.";
        if (!preserveStatus)
        {
            _status.Text = string.Empty;
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
            SetStatus(exception.Message);
        }
        finally
        {
            _busy = false;
            UpdateActions();
        }
    }

    private void SetStatus(string text)
    {
        _status.Text = text;
        var peer = UIElementAutomationPeer.FromElement(_status) ??
                   UIElementAutomationPeer.CreatePeerForElement(_status);
        peer?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
    }

    private void UpdateActions()
    {
        var isManager = CurrentUserIsAccessManager();
        var selected = _members.SelectedItem as MemberRow;
        _inviteSteamId.IsEnabled = !_busy && isManager;
        _inviteButton.IsEnabled = !_busy && isManager;
        _revokeButton.IsEnabled = !_busy && isManager && selected is { IsAccessManager: false };
        _transferButton.IsEnabled = !_busy && isManager &&
                                    selected is { IsAccessManager: false } &&
                                    selected.Member.Status == RemoteWorldMemberStatus.Active;
        _leaveButton.IsEnabled = !_busy && !isManager;
    }

    private bool CurrentUserIsAccessManager()
        => string.Equals(_accessManager.Provider, _currentUser.Provider, StringComparison.Ordinal) &&
           string.Equals(_accessManager.ExternalId, _currentUser.ExternalId, StringComparison.Ordinal);

    private static bool IdentityEquals(StewardRemoteIdentity left, StewardRemoteIdentity right)
        => string.Equals(left.Provider, right.Provider, StringComparison.Ordinal) &&
           string.Equals(left.ExternalId, right.ExternalId, StringComparison.Ordinal);

    private sealed record MemberRow(
        StewardRemoteWorldMember Member,
        bool IsAccessManager)
    {
        public string DisplayText =>
            $"Steam ID {Member.Identity.ExternalId}  •  " +
            (IsAccessManager ? "Access Manager" : Member.Status.ToString());
    }
}
