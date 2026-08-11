using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Media;
using SharedWorlds.Core.Domain;
using SharedWorlds.Infrastructure.Sessions;

namespace SharedWorlds.Desktop;

/// <summary>
/// Access surface for persistent peer Worlds. Canonical membership is the only authority here; Steam
/// lobby invitations are delivery after membership persistence. A live holder removal first revokes the
/// exact member/generation transport boundary, then persists canonical membership. A non-holder Leave
/// World is the inverse remote transaction: the current holder removes this authenticated member first,
/// then this Steward installation deletes its local replica and detaches from the Steam lobby.
/// </summary>
internal sealed class PeerWorldAccessDialog : Window
{
    private readonly StewardDesktopPeerRuntime _runtime;
    private World _world;
    private IReadOnlyList<UserIdentity> _displayedMembers = Array.Empty<UserIdentity>();
    private readonly ListBox _members = new();
    private readonly TextBox _steamId = new();
    private readonly Button _addButton = new()
    {
        Content = "Add person",
        Padding = new Thickness(12, 6, 12, 6)
    };
    private readonly Button _removeButton = new()
    {
        Content = "Remove access",
        Padding = new Thickness(12, 6, 12, 6),
        IsEnabled = false
    };
    private readonly Button _leaveButton = new()
    {
        Content = "Leave World",
        Padding = new Thickness(12, 6, 12, 6),
        IsEnabled = false
    };
    private readonly TextBlock _summary = new()
    {
        TextWrapping = TextWrapping.Wrap,
        Opacity = 0.78
    };
    private readonly TextBlock _status = new()
    {
        TextWrapping = TextWrapping.Wrap,
        Opacity = 0.82
    };
    private bool _canManage;
    private bool _canLeave;
    private bool _busy;

    public PeerWorldAccessDialog(
        StewardDesktopPeerRuntime runtime,
        World world)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(world);
        _runtime = runtime;
        _world = world;

        Title = $"Lobby — {world.Name}";
        Width = 560;
        Height = 520;
        MinWidth = 480;
        MinHeight = 420;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        UseLayoutRounding = true;
        SnapsToDevicePixels = true;
        Background = (Brush)Application.Current.FindResource("AppBackgroundBrush");
        Foreground = (Brush)Application.Current.FindResource("TextBrush");
        Content = BuildContent();

        _addButton.Click += AddButton_Click;
        _removeButton.Click += RemoveButton_Click;
        _leaveButton.Click += LeaveButton_Click;
        _members.SelectionChanged += (_, _) => UpdateActionState();
        Loaded += async (_, _) => await ReloadAsync();
    }

    public bool WorldWasLeft { get; private set; }
    public string? LeaveWarning { get; private set; }

    private UIElement BuildContent()
    {
        var root = new Grid { Margin = new Thickness(20) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
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

        var addRow = new Grid { Margin = new Thickness(0, 18, 0, 12) };
        addRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        addRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _steamId.MinHeight = 32;
        _steamId.VerticalContentAlignment = VerticalAlignment.Center;
        _steamId.ToolTip = "Steam ID64 of the person to add";
        AutomationProperties.SetName(_steamId, "Steam ID64 to add");
        AutomationProperties.SetHelpText(
            _steamId,
            "Enter the numeric Steam ID64 of a person who should become a member of this World.");
        addRow.Children.Add(_steamId);

        _addButton.Margin = new Thickness(10, 0, 0, 0);
        AutomationProperties.SetHelpText(
            _addButton,
            "Persist World membership first, then deliver a private Steam lobby invitation when possible.");
        Grid.SetColumn(_addButton, 1);
        addRow.Children.Add(_addButton);
        Grid.SetRow(addRow, 1);
        root.Children.Add(addRow);

        var memberArea = new Grid();
        memberArea.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        memberArea.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _members.Background = (Brush)FindResource("PanelBrush");
        _members.Foreground = (Brush)FindResource("TextBrush");
        _members.BorderBrush = (Brush)FindResource("BorderBrush");
        AutomationProperties.SetName(_members, "World members");
        memberArea.Children.Add(_members);

        var actions = new Grid { Margin = new Thickness(0, 10, 0, 0) };
        actions.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        actions.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        actions.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        AutomationProperties.SetHelpText(
            _leaveButton,
            "Ask the confirmed current host to remove this account canonically, then delete this local replica and leave the Steam lobby. Current holders must hand off host first.");
        actions.Children.Add(_leaveButton);

        AutomationProperties.SetHelpText(
            _removeButton,
            "Remove the selected non-holder member. During a live Host, Steward revokes that member's active peer transfer and game sessions before canonical removal. Host handoff blocks removal.");
        Grid.SetColumn(_removeButton, 2);
        actions.Children.Add(_removeButton);
        Grid.SetRow(actions, 1);
        memberArea.Children.Add(actions);
        Grid.SetRow(memberArea, 2);
        root.Children.Add(memberArea);

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
        Grid.SetRow(footer, 3);
        root.Children.Add(footer);

        return root;
    }

    private async void AddButton_Click(object sender, RoutedEventArgs e)
    {
        var value = _steamId.Text.Trim();
        if (!ulong.TryParse(
                value,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var steamId) ||
            steamId == 0)
        {
            SetStatus("Enter the person's numeric Steam ID64.");
            return;
        }

        if (SameUser(
                _runtime.User,
                new UserIdentity("steam", value, value)))
        {
            SetStatus("This Steam account is already the local Steward identity.");
            return;
        }

        await RunAsync(async () =>
        {
            var member = new UserIdentity(
                "steam",
                value,
                $"Steam ID {value}");

            // Canonical membership is committed before any platform invitation attempt. A later Steam
            // delivery failure therefore remains safely retryable and cannot make access ambiguous.
            _world = await _runtime.Membership.AddMemberAsync(
                _world.Id,
                _runtime.User,
                member);

            PeerWorldMemberInvitationResult? delivery = null;
            try
            {
                delivery = await _runtime.Invitations.InviteCanonicalMembersAsync(
                    _world.Id,
                    _runtime.User);
            }
            catch
            {
                // Membership is already canonical. Host Ready will retry all canonical members later.
            }

            _steamId.Clear();
            SetStatus(delivery is { Delivered: > 0 }
                ? $"Access added for Steam ID {value}. Private Steam invitation delivery was requested."
                : $"Access added for Steam ID {value}. Steward will retry the Steam invitation when this World is hosted.");
        });
    }

    private async void RemoveButton_Click(object sender, RoutedEventArgs e)
    {
        if (!TryGetSelectedRemovableMember(out var member))
        {
            return;
        }

        var confirmation = MessageBox.Show(
            this,
            $"Remove access for {FormatMember(member)}?\n\nIf this World is live, Steward will revoke that member's active peer transfer and game sessions immediately before removing canonical access. Remove access is refused while a host handoff is in progress.",
            "Remove World access",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);
        if (confirmation != MessageBoxResult.Yes)
        {
            return;
        }

        await RunAsync(async () =>
        {
            _world = await _runtime.MemberRemoval.RemoveMemberAsync(
                _world.Id,
                _runtime.User,
                member);
            SetStatus($"Access removed for {FormatMember(member)}. Active peer sessions were revoked when applicable, and future Host/Join admission will reject that member.");
        });
    }

    private async void LeaveButton_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || !_canLeave)
        {
            return;
        }

        var confirmation = MessageBox.Show(
            this,
            "Leave this World?\n\nSteward will ask the confirmed current host to remove this account from canonical membership. Only after the host confirms that removal will Steward delete this local World replica and leave the Steam lobby. This cannot run during host handoff.",
            "Leave World",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);
        if (confirmation != MessageBoxResult.Yes)
        {
            return;
        }

        _busy = true;
        UpdateActionState();
        try
        {
            var leave = new PeerWorldLeaveService(
                _runtime.Storage,
                _runtime.Lobby,
                _runtime.LeaveRequests,
                _runtime.User);
            var completion = await leave.LeaveAsync(_world.Id);

            WorldWasLeft = true;
            LeaveWarning = completion.CleanupWarning;
            DialogResult = true;
        }
        catch (Exception exception)
        {
            SetStatus(exception.Message);
            _busy = false;
            try
            {
                await ReloadAsync(preserveStatus: true);
            }
            catch
            {
                _canManage = false;
                _canLeave = false;
                UpdateActionState();
            }
        }
    }

    private async Task ReloadAsync(bool preserveStatus = false)
    {
        _world = await _runtime.Storage.LoadWorldAsync(_world.Id)
            ?? throw new InvalidOperationException(
                "The canonical peer World is no longer available on this Steward installation.");

        _displayedMembers = _world.Members
            .OrderBy(member => SameUser(member, _runtime.User) ? 0 : 1)
            .ThenBy(member => member.Provider, StringComparer.OrdinalIgnoreCase)
            .ThenBy(member => member.ExternalId, StringComparer.Ordinal)
            .ToArray();
        _members.ItemsSource = _displayedMembers
            .Select(FormatMember)
            .ToArray();

        var isCanonicalMember = _world.Members.Any(member => SameUser(member, _runtime.User));
        var authority = _world.PeerAuthority;
        _canManage = authority is not null &&
                     SameUser(authority.Holder, _runtime.User) &&
                     isCanonicalMember;
        var isNonHolderMember = authority is not null &&
                                !SameUser(authority.Holder, _runtime.User) &&
                                isCanonicalMember;
        _canLeave = false;
        if (isNonHolderMember)
        {
            try
            {
                var liveLobby = await _runtime.Lobby.GetAsync(_world.Id);
                _canLeave = liveLobby is not null &&
                            liveLobby.OwnerConfirmed &&
                            liveLobby.AuthorityGeneration == authority!.Generation &&
                            liveLobby.RequestedHost is null &&
                            SameUser(liveLobby.Owner, authority.Holder);
            }
            catch
            {
                _canLeave = false;
            }
        }

        _summary.Text = _canManage
            ? "World membership is stored with the World. Add grants access before Steam invite delivery. Remove access also works during a live Host by revoking the selected member's peer sessions before canonical removal. To leave this World yourself, hand off host authority first."
            : _canLeave
                ? "This account is a canonical World member but not the current authority holder. Leave World asks the active holder to remove this account first; Steward deletes the local replica only after that authoritative acknowledgement."
                : isNonHolderMember
                    ? "This account is a canonical non-holder member. Leave World becomes available when Steward is attached to the confirmed current host at this exact authority generation and no handoff is in progress."
                    : "World membership is read-only here because this Steward identity is not a current canonical member or authority holder.";
        UpdateActionState();
        if (!preserveStatus)
        {
            _status.Text = string.Empty;
        }
    }

    private async Task RunAsync(Func<Task> operation)
    {
        if (_busy)
        {
            return;
        }

        _busy = true;
        UpdateActionState();
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
            try
            {
                await ReloadAsync(preserveStatus: true);
            }
            catch
            {
                _canManage = false;
                _canLeave = false;
                UpdateActionState();
            }
        }
    }

    private void UpdateActionState()
    {
        var canMutate = !_busy && _canManage;
        _steamId.IsEnabled = canMutate;
        _addButton.IsEnabled = canMutate;
        _removeButton.IsEnabled = canMutate &&
                                  TryGetSelectedRemovableMember(out _);
        _leaveButton.IsEnabled = !_busy && _canLeave;
    }

    private bool TryGetSelectedRemovableMember(out UserIdentity member)
    {
        var index = _members.SelectedIndex;
        if (index >= 0 &&
            index < _displayedMembers.Count &&
            !SameUser(_displayedMembers[index], _runtime.User))
        {
            member = _displayedMembers[index];
            return true;
        }

        member = null!;
        return false;
    }

    private string FormatMember(UserIdentity member)
    {
        var label = string.Equals(member.Provider, "steam", StringComparison.OrdinalIgnoreCase)
            ? $"Steam ID {member.ExternalId}"
            : $"{member.Provider}: {member.ExternalId}";
        return SameUser(member, _runtime.User)
            ? $"{label} — this account"
            : label;
    }

    private void SetStatus(string text)
    {
        _status.Text = text;
        var peer = UIElementAutomationPeer.FromElement(_status) ??
                   UIElementAutomationPeer.CreatePeerForElement(_status);
        peer?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
    }

    private static bool SameUser(UserIdentity left, UserIdentity right)
        => string.Equals(left.Provider, right.Provider, StringComparison.OrdinalIgnoreCase) &&
           string.Equals(left.ExternalId, right.ExternalId, StringComparison.Ordinal);
}
