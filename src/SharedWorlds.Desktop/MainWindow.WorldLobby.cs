using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Threading;
using SharedWorlds.Infrastructure.Remote;

namespace SharedWorlds.Desktop;

public partial class MainWindow
{
    private Border? _worldLobbyCard;
    private StackPanel? _worldLobbyPlayingRows;
    private StackPanel? _worldLobbyGroupRows;
    private TextBlock? _worldLobbyStatus;
    private DispatcherTimer? _worldLobbyTimer;
    private bool _worldLobbyRefreshInProgress;
    private int _worldLobbyRefreshVersion;

    private void InitializeWorldLobbyUi()
    {
        if (_worldLobbyCard is not null)
        {
            return;
        }

        var card = new Border
        {
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(20),
            Margin = new Thickness(0, 22, 0, 0),
            Visibility = Visibility.Collapsed
        };
        card.SetResourceReference(Border.BackgroundProperty, "PanelBrush");
        card.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");
        AutomationProperties.SetName(card, "World lobby");

        var root = new StackPanel();
        root.Children.Add(new TextBlock
        {
            Text = "World Lobby",
            FontSize = 18,
            FontWeight = FontWeights.SemiBold
        });
        root.Children.Add(new TextBlock
        {
            Text = "Playing now",
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 16, 0, 7)
        });

        var playingRows = new StackPanel();
        root.Children.Add(playingRows);

        root.Children.Add(new TextBlock
        {
            Text = "World group",
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 16, 0, 7)
        });

        var groupRows = new StackPanel();
        root.Children.Add(groupRows);

        var status = new TextBlock
        {
            Margin = new Thickness(0, 12, 0, 0),
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap
        };
        status.SetResourceReference(TextBlock.ForegroundProperty, "MutedTextBrush");
        AutomationProperties.SetName(status, "World lobby status");
        AutomationProperties.SetLiveSetting(status, AutomationLiveSetting.Polite);
        RegisterLiveRegion(status);
        root.Children.Add(status);

        card.Child = root;
        WorldDetailsPanel.Children.Insert(Math.Min(3, WorldDetailsPanel.Children.Count), card);

        _worldLobbyCard = card;
        _worldLobbyPlayingRows = playingRows;
        _worldLobbyGroupRows = groupRows;
        _worldLobbyStatus = status;

        WorldList.SelectionChanged += async (_, _) => await RefreshWorldLobbyAsync();

        _worldLobbyTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(10)
        };
        _worldLobbyTimer.Tick += async (_, _) => await RefreshWorldLobbyAsync();
        _worldLobbyTimer.Start();
        Closed += (_, _) => _worldLobbyTimer?.Stop();
    }

    private async Task RefreshWorldLobbyAsync()
    {
        var card = _worldLobbyCard;
        var playingRows = _worldLobbyPlayingRows;
        var groupRows = _worldLobbyGroupRows;
        var status = _worldLobbyStatus;
        if (card is null || playingRows is null || groupRows is null || status is null)
        {
            return;
        }

        var selected = WorldList.SelectedItem as UnifiedWorldListItem;
        var world = selected?.World;
        var runtime = _remoteRuntime;
        var version = ++_worldLobbyRefreshVersion;

        if (world is null || !_remoteWorldIds.Contains(world.Id))
        {
            card.Visibility = Visibility.Collapsed;
            return;
        }

        card.Visibility = Visibility.Visible;
        if (runtime is null)
        {
            playingRows.Children.Clear();
            groupRows.Children.Clear();
            AddMutedLobbyRow(playingRows, "Playing status unavailable.");
            AddMutedLobbyRow(groupRows, "Reconnect Steward to load the World group.");
            status.Text = "Connection required for live lobby information.";
            return;
        }

        if (_worldLobbyRefreshInProgress)
        {
            return;
        }

        _worldLobbyRefreshInProgress = true;
        try
        {
            IReadOnlyList<StewardRemoteWorldMember> members;
            StewardRemoteWorldMetadata? metadata;
            StewardRemoteWorldPlayerPresenceSnapshot snapshot;
            IReadOnlyList<StewardRemoteNamedIdentity> namedIdentities = [];

            try
            {
                members = await runtime.Access.ListMembersAsync(world.Id);
                metadata = await runtime.GetWorldMetadataAsync(world.Id);
                snapshot = await runtime.PlayerPresence.GetSnapshotAsync(world.Id);

                if (string.Equals(runtime.User.Provider, "friends-build", StringComparison.Ordinal))
                {
                    namedIdentities = await runtime.Access.ListFriendsBuildIdentitiesAsync();
                }
            }
            catch (Exception exception) when (IsRemoteAvailabilityFailure(exception))
            {
                if (version == _worldLobbyRefreshVersion &&
                    (WorldList.SelectedItem as UnifiedWorldListItem)?.World.Id == world.Id)
                {
                    playingRows.Children.Clear();
                    groupRows.Children.Clear();
                    AddMutedLobbyRow(playingRows, "Playing status unavailable.");
                    AddMutedLobbyRow(groupRows, "World group temporarily unavailable.");
                    status.Text = "Steward cannot refresh the World lobby right now.";
                }

                return;
            }

            if (version != _worldLobbyRefreshVersion ||
                (WorldList.SelectedItem as UnifiedWorldListItem)?.World.Id != world.Id)
            {
                return;
            }

            var names = namedIdentities.ToDictionary(
                static identity => (identity.Provider, identity.ExternalId),
                static identity => identity.DisplayName);

            string ResolveName(string provider, string externalId)
            {
                if (string.Equals(runtime.User.Provider, provider, StringComparison.Ordinal) &&
                    string.Equals(runtime.User.ExternalId, externalId, StringComparison.Ordinal) &&
                    !string.IsNullOrWhiteSpace(runtime.User.DisplayName))
                {
                    return runtime.User.DisplayName;
                }

                return names.TryGetValue((provider, externalId), out var displayName) &&
                       !string.IsNullOrWhiteSpace(displayName)
                    ? displayName
                    : externalId;
            }

            var host = snapshot.Host;
            bool IsHost(string provider, string externalId)
                => host is not null &&
                   IsSameIdentity(provider, externalId, host.Provider, host.ExternalId);

            playingRows.Children.Clear();
            var playing = snapshot.Players
                .Select(player => new LobbyPlayerRow(
                    player.Provider,
                    player.ExternalId,
                    ResolveName(player.Provider, player.ExternalId),
                    IsHost(player.Provider, player.ExternalId)))
                .ToList();

            if (host is not null &&
                playing.All(row => !IsSameIdentity(
                    row.Provider,
                    row.ExternalId,
                    host.Provider,
                    host.ExternalId)))
            {
                playing.Add(new LobbyPlayerRow(
                    host.Provider,
                    host.ExternalId,
                    ResolveName(host.Provider, host.ExternalId),
                    IsHost: true));
            }

            foreach (var row in playing
                         .OrderByDescending(static row => row.IsHost)
                         .ThenBy(static row => row.DisplayName, StringComparer.OrdinalIgnoreCase))
            {
                var suffix = row.IsHost
                    ? host?.State == StewardRemoteHostPresenceState.Starting
                        ? " — HOST · starting"
                        : " — HOST"
                    : string.Empty;
                AddLobbyRow(playingRows, row.DisplayName + suffix);
            }

            if (playing.Count == 0)
            {
                AddMutedLobbyRow(playingRows, "No one is playing right now.");
            }

            groupRows.Children.Clear();
            var accessManager = metadata?.AccessManager;
            foreach (var member in members
                         .OrderByDescending(member => accessManager is not null &&
                             IsSameIdentity(
                                 member.Identity.Provider,
                                 member.Identity.ExternalId,
                                 accessManager.Provider,
                                 accessManager.ExternalId))
                         .ThenBy(member => ResolveName(member.Identity.Provider, member.Identity.ExternalId),
                             StringComparer.OrdinalIgnoreCase))
            {
                var isManager = accessManager is not null &&
                                IsSameIdentity(
                                    member.Identity.Provider,
                                    member.Identity.ExternalId,
                                    accessManager.Provider,
                                    accessManager.ExternalId);
                var suffix = isManager ? " — Access Manager" : string.Empty;
                if (member.Status == RemoteWorldMemberStatus.RevocationPending)
                {
                    suffix += " · access removal pending";
                }

                AddLobbyRow(
                    groupRows,
                    ResolveName(member.Identity.Provider, member.Identity.ExternalId) + suffix);
            }

            if (members.Count == 0)
            {
                AddMutedLobbyRow(groupRows, "No World members found.");
            }

            status.Text = $"{playing.Count} playing · {members.Count} in World group";
        }
        finally
        {
            _worldLobbyRefreshInProgress = false;
        }
    }

    private static bool IsSameIdentity(
        string leftProvider,
        string leftExternalId,
        string rightProvider,
        string rightExternalId)
        => string.Equals(leftProvider, rightProvider, StringComparison.Ordinal) &&
           string.Equals(leftExternalId, rightExternalId, StringComparison.Ordinal);

    private static void AddLobbyRow(Panel panel, string text)
    {
        var row = new TextBlock
        {
            Text = text,
            FontSize = 13,
            Margin = new Thickness(0, 2, 0, 2),
            TextWrapping = TextWrapping.Wrap
        };
        AutomationProperties.SetName(row, text);
        panel.Children.Add(row);
    }

    private static void AddMutedLobbyRow(Panel panel, string text)
    {
        var row = new TextBlock
        {
            Text = text,
            FontSize = 12,
            Margin = new Thickness(0, 2, 0, 2),
            TextWrapping = TextWrapping.Wrap
        };
        row.SetResourceReference(TextBlock.ForegroundProperty, "MutedTextBrush");
        AutomationProperties.SetName(row, text);
        panel.Children.Add(row);
    }

    private sealed record LobbyPlayerRow(
        string Provider,
        string ExternalId,
        string DisplayName,
        bool IsHost);
}
