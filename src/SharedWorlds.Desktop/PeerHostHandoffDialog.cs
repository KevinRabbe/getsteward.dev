using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using SharedWorlds.Core.Domain;

namespace SharedWorlds.Desktop;

/// <summary>
/// Selects one already-canonical remote Steam member who is currently present in the exact live lobby
/// as the requested next peer host. This dialog does not change authority. The lifecycle/session
/// coordinator still revalidates the target before stopping the outgoing host, committing the final
/// revision, transferring generation N+1, and moving lobby ownership.
/// </summary>
internal sealed class PeerHostHandoffDialog : Window
{
    private readonly IReadOnlyList<UserIdentity> _candidates;
    private readonly ListBox _list = new();
    private readonly Button _confirm = new()
    {
        Content = "Hand off host",
        Padding = new Thickness(14, 6, 14, 6),
        IsEnabled = false
    };

    public PeerHostHandoffDialog(
        World world,
        UserIdentity localHolder,
        IReadOnlyList<UserIdentity> liveLobbyMembers)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(localHolder);
        ArgumentNullException.ThrowIfNull(liveLobbyMembers);

        var liveSteamIds = liveLobbyMembers
            .Where(IsSteamAuthorityCandidate)
            .Select(member => member.ExternalId)
            .ToHashSet(StringComparer.Ordinal);
        _candidates = world.Members
            .Where(member =>
                !SameUser(member, localHolder) &&
                IsSteamAuthorityCandidate(member) &&
                liveSteamIds.Contains(member.ExternalId))
            .OrderBy(member => member.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(member => member.ExternalId, StringComparer.Ordinal)
            .ToArray();

        Title = $"Hand off host — {world.Name}";
        Width = 500;
        Height = 390;
        MinWidth = 430;
        MinHeight = 330;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = (Brush)Application.Current.FindResource("AppBackgroundBrush");
        Foreground = (Brush)Application.Current.FindResource("TextBrush");
        Content = BuildContent(world.Name);

        _list.SelectionChanged += (_, _) =>
            _confirm.IsEnabled = _list.SelectedIndex >= 0 &&
                                 _list.SelectedIndex < _candidates.Count;
        _confirm.Click += (_, _) =>
        {
            if (SelectedHost is null)
            {
                return;
            }

            DialogResult = true;
        };
    }

    public UserIdentity? SelectedHost
    {
        get
        {
            var index = _list.SelectedIndex;
            return index >= 0 && index < _candidates.Count
                ? _candidates[index]
                : null;
        }
    }

    private UIElement BuildContent(string worldName)
    {
        var root = new Grid { Margin = new Thickness(20) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var header = new StackPanel();
        header.Children.Add(new TextBlock
        {
            Text = "Choose the next host",
            FontSize = 22,
            FontWeight = FontWeights.SemiBold
        });
        header.Children.Add(new TextBlock
        {
            Text = $"Steward is showing only canonical members currently connected to the live private lobby for '{worldName}'. The current host will stop safely, save the final World state, transfer that exact revision, and only then move authority. Steward rechecks the selected member before handoff begins.",
            Margin = new Thickness(0, 7, 0, 16),
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.78
        });
        Grid.SetRow(header, 0);
        root.Children.Add(header);

        _list.ItemsSource = _candidates
            .Select(FormatMember)
            .ToArray();
        _list.Background = (Brush)FindResource("PanelBrush");
        _list.Foreground = (Brush)FindResource("TextBrush");
        _list.BorderBrush = (Brush)FindResource("BorderBrush");
        AutomationProperties.SetName(_list, "Eligible live World members");
        AutomationProperties.SetHelpText(
            _list,
            "Choose a canonical Steam World member who is currently connected to this exact live private Steam lobby.");
        Grid.SetRow(_list, 1);
        root.Children.Add(_list);

        var footer = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 16, 0, 0)
        };
        var cancel = new Button
        {
            Content = DesktopText.Close,
            Padding = new Thickness(14, 6, 14, 6),
            Margin = new Thickness(0, 0, 10, 0)
        };
        cancel.Click += (_, _) => DialogResult = false;
        footer.Children.Add(cancel);
        footer.Children.Add(_confirm);
        Grid.SetRow(footer, 2);
        root.Children.Add(footer);

        if (_candidates.Count == 0)
        {
            _confirm.IsEnabled = false;
            _list.ItemsSource = new[]
            {
                "No other canonical Steam member is currently connected to this live lobby."
            };
        }

        return root;
    }

    private static string FormatMember(UserIdentity member)
    {
        var steamIdLabel = $"Steam ID {member.ExternalId}";
        return string.IsNullOrWhiteSpace(member.DisplayName) ||
               string.Equals(member.DisplayName, member.ExternalId, StringComparison.Ordinal)
            ? steamIdLabel
            : $"{member.DisplayName} — {steamIdLabel}";
    }

    private static bool IsSteamAuthorityCandidate(UserIdentity member)
        => string.Equals(member.Provider, "steam", StringComparison.OrdinalIgnoreCase) &&
           ulong.TryParse(
               member.ExternalId,
               NumberStyles.None,
               CultureInfo.InvariantCulture,
               out var steamId) &&
           steamId != 0;

    private static bool SameUser(UserIdentity left, UserIdentity right)
        => string.Equals(left.Provider, right.Provider, StringComparison.OrdinalIgnoreCase) &&
           string.Equals(left.ExternalId, right.ExternalId, StringComparison.Ordinal);
}
