using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Threading;
using SharedWorlds.Infrastructure.Remote;

namespace SharedWorlds.Desktop;

public partial class MainWindow
{
    private const double TopNavigationButtonWidth = 96;
    private const double SidebarActionButtonWidth = 112;
    private const double WorldActionButtonWidth = 128;

    private Button? _globalLobbyButton;
    private Border? _globalLobbyPanel;
    private StackPanel? _globalLobbyRows;
    private TextBlock? _globalLobbyStatus;
    private DispatcherTimer? _globalLobbyTimer;
    private bool _globalLobbyVisible;
    private bool _globalLobbyRefreshInProgress;
    private int _globalLobbyRefreshVersion;

    internal void InitializeProfessionalProductShell()
    {
        NormalizeTopNavigation();
        NormalizeSelectedGameActions();
        NormalizeWorldActions();
        InitializeGlobalLobbySurface();
        AddBuildIdentityToMoreMenu();
    }

    private void NormalizeTopNavigation()
    {
        var topBar = FindSafeWorldTopBar();
        if (topBar is null || _globalLobbyButton is not null)
        {
            return;
        }

        if (_globalSettingsButton is not null)
        {
            DetachSafeWorldSidebarElement(_globalSettingsButton);
        }

        if (_moreButton is not null)
        {
            DetachSafeWorldSidebarElement(_moreButton);
        }

        var lobbyButton = new Button
        {
            Content = DesktopText.Lobby,
            Width = TopNavigationButtonWidth,
            Height = 40,
            Margin = new Thickness(0, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        AutomationProperties.SetName(lobbyButton, DesktopText.Lobby);
        AutomationProperties.SetHelpText(
            lobbyButton,
            "Open the cross-game Lobby for your shared Worlds.");
        lobbyButton.Click += async (_, _) => await ShowGlobalLobbyAsync();
        _globalLobbyButton = lobbyButton;

        var navigation = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center
        };
        navigation.Children.Add(lobbyButton);

        if (_moreButton is not null)
        {
            _moreButton.Content = DesktopText.More;
            _moreButton.Width = TopNavigationButtonWidth;
            _moreButton.Height = 40;
            _moreButton.MinWidth = 0;
            _moreButton.Padding = new Thickness(12, 6, 12, 6);
            _moreButton.Margin = new Thickness(0, 0, 8, 0);
            navigation.Children.Add(_moreButton);
        }

        if (_globalSettingsButton is not null)
        {
            _globalSettingsButton.Width = TopNavigationButtonWidth;
            _globalSettingsButton.Height = 40;
            _globalSettingsButton.Padding = new Thickness(12, 6, 12, 6);
            _globalSettingsButton.Margin = new Thickness(0);
            navigation.Children.Add(_globalSettingsButton);
        }

        DockPanel.SetDock(navigation, Dock.Right);
        topBar.Children.Add(navigation);
        RefreshButton.IsEnabledChanged += (_, _) => UpdateGlobalLobbyButtonState();
        UpdateGlobalLobbyButtonState();
    }

    private void NormalizeSelectedGameActions()
    {
        NormalizeActionButton(BackToGamesButton, SidebarActionButtonWidth, 40);
        NormalizeActionButton(OpenImportButton, SidebarActionButtonWidth, 40);
        if (_createWorldButton is not null)
        {
            NormalizeActionButton(_createWorldButton, SidebarActionButtonWidth, 40);
        }
    }

    private void NormalizeWorldActions()
    {
        NormalizeActionButton(ContinueButton, WorldActionButtonWidth, 42);
        NormalizeActionButton(HostButton, WorldActionButtonWidth, 42);
        NormalizeActionButton(StopHostingButton, WorldActionButtonWidth, 42);
        NormalizeActionButton(ShareButton, WorldActionButtonWidth, 42);
        NormalizeActionButton(VerifyEnvironmentButton, WorldActionButtonWidth, 40);
        NormalizeActionButton(RepairEnvironmentButton, WorldActionButtonWidth, 40);

        if (_joinButton is not null)
        {
            NormalizeActionButton(_joinButton, WorldActionButtonWidth, 42);
        }

        if (_shareCopyButton is not null)
        {
            NormalizeActionButton(_shareCopyButton, WorldActionButtonWidth, 42);
        }

        if (_deleteWorldButton is not null)
        {
            NormalizeActionButton(_deleteWorldButton, WorldActionButtonWidth, 40);
            if (TryFindResource("DangerButtonStyle") is Style dangerStyle)
            {
                _deleteWorldButton.Style = dangerStyle;
            }
        }
    }

    private static void NormalizeActionButton(Button button, double width, double height)
    {
        button.Width = width;
        button.Height = height;
        button.MinWidth = 0;
        button.MinHeight = 0;
        button.Padding = new Thickness(12, 6, 12, 6);
    }

    private void InitializeGlobalLobbySurface()
    {
        if (_globalLobbyPanel is not null)
        {
            return;
        }

        var panel = new Border
        {
            Visibility = Visibility.Collapsed,
            Padding = new Thickness(32, 28, 32, 32),
            Focusable = true
        };
        panel.SetResourceReference(Border.BackgroundProperty, "AppBackgroundBrush");
        Grid.SetColumn(panel, 0);
        Grid.SetColumnSpan(panel, 2);
        Panel.SetZIndex(panel, 110);

        var scroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        };
        var content = new StackPanel
        {
            MaxWidth = 920,
            HorizontalAlignment = HorizontalAlignment.Left
        };

        var backButton = new Button
        {
            Content = DesktopText.BackToGames,
            Width = SidebarActionButtonWidth,
            Height = 40,
            Margin = new Thickness(0, 0, 0, 20),
            HorizontalAlignment = HorizontalAlignment.Left
        };
        backButton.Click += (_, _) => HideGlobalLobby(showGames: true);
        content.Children.Add(backButton);

        content.Children.Add(new TextBlock
        {
            Text = DesktopText.Lobby,
            FontSize = 30,
            FontWeight = FontWeights.SemiBold
        });

        var description = new TextBlock
        {
            Text = DesktopText.LobbyDescription,
            Margin = new Thickness(0, 6, 0, 24),
            FontSize = 13,
            TextWrapping = TextWrapping.Wrap
        };
        description.SetResourceReference(TextBlock.ForegroundProperty, "MutedTextBrush");
        content.Children.Add(description);

        var rows = new StackPanel();
        content.Children.Add(rows);

        var status = new TextBlock
        {
            Margin = new Thickness(0, 8, 0, 0),
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap
        };
        status.SetResourceReference(TextBlock.ForegroundProperty, "MutedTextBrush");
        AutomationProperties.SetLiveSetting(status, AutomationLiveSetting.Polite);
        RegisterLiveRegion(status);
        content.Children.Add(status);

        scroll.Content = content;
        panel.Child = scroll;
        WorkspaceGrid.Children.Add(panel);

        _globalLobbyPanel = panel;
        _globalLobbyRows = rows;
        _globalLobbyStatus = status;

        _globalLobbyTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(15)
        };
        _globalLobbyTimer.Tick += async (_, _) =>
        {
            if (_globalLobbyVisible)
            {
                await RefreshGlobalLobbyAsync();
            }
        };
        _globalLobbyTimer.Start();
        Closed += (_, _) => _globalLobbyTimer?.Stop();
    }

    private async Task ShowGlobalLobbyAsync()
    {
        if (_globalLobbyPanel is null)
        {
            return;
        }

        if (_globalSettingsVisible)
        {
            HideGlobalSettings();
        }

        _globalLobbyVisible = true;
        _globalLobbyPanel.Visibility = Visibility.Visible;
        GamesLibraryPanel.IsEnabled = false;
        WorldSidebar.IsEnabled = false;
        WorldDetailsScroll.IsEnabled = false;
        UpdateGlobalLobbyButtonState();
        _globalLobbyPanel.Focus();
        await RefreshGlobalLobbyAsync();
    }

    private void HideGlobalLobby(bool showGames)
    {
        if (_globalLobbyPanel is null)
        {
            return;
        }

        _globalLobbyVisible = false;
        _globalLobbyPanel.Visibility = Visibility.Collapsed;
        GamesLibraryPanel.IsEnabled = true;
        WorldSidebar.IsEnabled = true;
        WorldDetailsScroll.IsEnabled = true;
        UpdateGlobalLobbyButtonState();

        if (showGames)
        {
            ShowGamesLibrary(focusLibrary: true);
        }
        else
        {
            ApplyGameNavigationLayout();
        }
    }

    private void UpdateGlobalLobbyButtonState()
    {
        if (_globalLobbyButton is not null)
        {
            _globalLobbyButton.IsEnabled = !_globalLobbyVisible && RefreshButton.IsEnabled;
        }
    }

    private async Task RefreshGlobalLobbyAsync()
    {
        if (!_globalLobbyVisible ||
            _globalLobbyRefreshInProgress ||
            _globalLobbyRows is null ||
            _globalLobbyStatus is null)
        {
            return;
        }

        var refreshVersion = ++_globalLobbyRefreshVersion;
        _globalLobbyRefreshInProgress = true;
        _globalLobbyStatus.Text = "Refreshing…";

        try
        {
            var rows = _globalLobbyRows;
            rows.Children.Clear();

            var sharedWorlds = _allWorldItems
                .Where(item => _remoteWorldIds.Contains(item.World.Id))
                .OrderBy(item => item.GameName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (sharedWorlds.Count == 0)
            {
                AddGlobalLobbyEmptyState(rows, DesktopText.NoSharedWorlds);
                _globalLobbyStatus.Text = string.Empty;
                return;
            }

            var runtime = _remoteRuntime;
            if (runtime is null)
            {
                AddGlobalLobbyEmptyState(rows, "Reconnect Safe World to load your shared World lobbies.");
                _globalLobbyStatus.Text = "Connection required.";
                return;
            }

            IReadOnlyDictionary<(string Provider, string ExternalId), string> names =
                new Dictionary<(string Provider, string ExternalId), string>();
            if (string.Equals(runtime.User.Provider, "friends-build", StringComparison.Ordinal))
            {
                try
                {
                    names = (await runtime.Access.ListFriendsBuildIdentitiesAsync())
                        .ToDictionary(
                            static identity => (identity.Provider, identity.ExternalId),
                            static identity => identity.DisplayName);
                }
                catch (Exception exception) when (IsRemoteAvailabilityFailure(exception))
                {
                    // Names are presentation-only. Individual World cards still remain useful with
                    // provider identities if the optional Friends Build roster is temporarily unavailable.
                }
            }

            var activeWorldCount = 0;
            foreach (var item in sharedWorlds)
            {
                if (refreshVersion != _globalLobbyRefreshVersion || !_globalLobbyVisible)
                {
                    return;
                }

                try
                {
                    var members = await runtime.Access.ListMembersAsync(item.World.Id);
                    var metadata = await runtime.GetWorldMetadataAsync(item.World.Id);
                    var snapshot = await runtime.PlayerPresence.GetSnapshotAsync(item.World.Id);
                    var isActive = snapshot.Host is not null || snapshot.Players.Count > 0;
                    if (isActive)
                    {
                        activeWorldCount++;
                    }

                    rows.Children.Add(CreateGlobalLobbyWorldCard(
                        item,
                        members,
                        metadata,
                        snapshot,
                        names,
                        runtime.User.Provider,
                        runtime.User.ExternalId,
                        runtime.User.DisplayName));
                }
                catch (Exception exception) when (IsRemoteAvailabilityFailure(exception))
                {
                    rows.Children.Add(CreateUnavailableLobbyWorldCard(item));
                }
            }

            _globalLobbyStatus.Text = activeWorldCount == 0
                ? $"{sharedWorlds.Count} shared Worlds"
                : $"{sharedWorlds.Count} shared Worlds · {activeWorldCount} active now";
        }
        finally
        {
            _globalLobbyRefreshInProgress = false;
        }
    }

    private Border CreateGlobalLobbyWorldCard(
        UnifiedWorldListItem item,
        IReadOnlyList<StewardRemoteWorldMember> members,
        StewardRemoteWorldMetadata? metadata,
        StewardRemoteWorldPlayerPresenceSnapshot snapshot,
        IReadOnlyDictionary<(string Provider, string ExternalId), string> names,
        string currentProvider,
        string currentExternalId,
        string? currentDisplayName)
    {
        var card = CreateLobbyCardShell();
        var root = new StackPanel();
        card.Child = root;

        var header = new DockPanel
        {
            LastChildFill = true
        };
        var openButton = new Button
        {
            Content = DesktopText.OpenWorld,
            Width = SidebarActionButtonWidth,
            Height = 38,
            Margin = new Thickness(16, 0, 0, 0),
            Tag = item,
            VerticalAlignment = VerticalAlignment.Top
        };
        openButton.Click += GlobalLobbyOpenWorldButton_Click;
        DockPanel.SetDock(openButton, Dock.Right);
        header.Children.Add(openButton);

        var title = new StackPanel();
        title.Children.Add(new TextBlock
        {
            Text = item.Name,
            FontSize = 18,
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap
        });
        var game = new TextBlock
        {
            Text = item.GameName,
            Margin = new Thickness(0, 3, 0, 0),
            FontSize = 12
        };
        game.SetResourceReference(TextBlock.ForegroundProperty, "MutedTextBrush");
        title.Children.Add(game);
        header.Children.Add(title);
        root.Children.Add(header);

        root.Children.Add(CreateLobbySectionHeading(DesktopText.PlayingNow));
        var playingNames = BuildPlayingNames(
            snapshot,
            names,
            currentProvider,
            currentExternalId,
            currentDisplayName);
        root.Children.Add(CreateLobbyValueText(
            playingNames.Count == 0
                ? "No one is playing right now."
                : string.Join("  ·  ", playingNames),
            muted: playingNames.Count == 0));

        root.Children.Add(CreateLobbySectionHeading(DesktopText.WorldGroup));
        var accessManager = metadata?.AccessManager;
        var memberNames = members
            .OrderByDescending(member => accessManager is not null && IsGlobalLobbySameIdentity(
                member.Identity.Provider,
                member.Identity.ExternalId,
                accessManager.Provider,
                accessManager.ExternalId))
            .ThenBy(member => ResolveGlobalLobbyName(
                member.Identity.Provider,
                member.Identity.ExternalId,
                names,
                currentProvider,
                currentExternalId,
                currentDisplayName), StringComparer.OrdinalIgnoreCase)
            .Select(member =>
            {
                var displayName = ResolveGlobalLobbyName(
                    member.Identity.Provider,
                    member.Identity.ExternalId,
                    names,
                    currentProvider,
                    currentExternalId,
                    currentDisplayName);
                var suffix = accessManager is not null && IsGlobalLobbySameIdentity(
                    member.Identity.Provider,
                    member.Identity.ExternalId,
                    accessManager.Provider,
                    accessManager.ExternalId)
                    ? DesktopText.AccessManagerSuffix
                    : string.Empty;
                if (member.Status == RemoteWorldMemberStatus.RevocationPending)
                {
                    suffix += DesktopText.AccessRemovalPendingSuffix;
                }

                return displayName + suffix;
            })
            .ToList();
        root.Children.Add(CreateLobbyValueText(
            memberNames.Count == 0
                ? "No World members found."
                : string.Join("  ·  ", memberNames),
            muted: memberNames.Count == 0));

        return card;
    }

    private Border CreateUnavailableLobbyWorldCard(UnifiedWorldListItem item)
    {
        var card = CreateLobbyCardShell();
        var root = new StackPanel();
        card.Child = root;

        root.Children.Add(new TextBlock
        {
            Text = item.Name,
            FontSize = 18,
            FontWeight = FontWeights.SemiBold
        });
        var subtitle = new TextBlock
        {
            Text = $"{item.GameName} · Lobby temporarily unavailable",
            Margin = new Thickness(0, 4, 0, 0),
            FontSize = 12
        };
        subtitle.SetResourceReference(TextBlock.ForegroundProperty, "MutedTextBrush");
        root.Children.Add(subtitle);
        return card;
    }

    private Border CreateLobbyCardShell()
    {
        var card = new Border
        {
            Padding = new Thickness(20),
            Margin = new Thickness(0, 0, 0, 12),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12)
        };
        card.SetResourceReference(Border.BackgroundProperty, "PanelBrush");
        card.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");
        return card;
    }

    private static TextBlock CreateLobbySectionHeading(string text)
        => new()
        {
            Text = text,
            Margin = new Thickness(0, 16, 0, 5),
            FontSize = 12,
            FontWeight = FontWeights.SemiBold
        };

    private TextBlock CreateLobbyValueText(string text, bool muted)
    {
        var value = new TextBlock
        {
            Text = text,
            FontSize = 13,
            TextWrapping = TextWrapping.Wrap
        };
        if (muted)
        {
            value.SetResourceReference(TextBlock.ForegroundProperty, "MutedTextBrush");
        }

        return value;
    }

    private static List<string> BuildPlayingNames(
        StewardRemoteWorldPlayerPresenceSnapshot snapshot,
        IReadOnlyDictionary<(string Provider, string ExternalId), string> names,
        string currentProvider,
        string currentExternalId,
        string? currentDisplayName)
    {
        var host = snapshot.Host;
        var playing = snapshot.Players
            .Select(player => new
            {
                player.Provider,
                player.ExternalId,
                Name = ResolveGlobalLobbyName(
                    player.Provider,
                    player.ExternalId,
                    names,
                    currentProvider,
                    currentExternalId,
                    currentDisplayName),
                IsHost = host is not null && IsGlobalLobbySameIdentity(
                    player.Provider,
                    player.ExternalId,
                    host.Provider,
                    host.ExternalId)
            })
            .ToList();

        if (host is not null && playing.All(player => !IsGlobalLobbySameIdentity(
                player.Provider,
                player.ExternalId,
                host.Provider,
                host.ExternalId)))
        {
            playing.Add(new
            {
                host.Provider,
                host.ExternalId,
                Name = ResolveGlobalLobbyName(
                    host.Provider,
                    host.ExternalId,
                    names,
                    currentProvider,
                    currentExternalId,
                    currentDisplayName),
                IsHost = true
            });
        }

        return playing
            .OrderByDescending(static player => player.IsHost)
            .ThenBy(static player => player.Name, StringComparer.OrdinalIgnoreCase)
            .Select(player => player.Name + (player.IsHost
                ? host?.State == StewardRemoteHostPresenceState.Starting
                    ? DesktopText.HostStartingSuffix
                    : DesktopText.HostSuffix
                : string.Empty))
            .ToList();
    }

    private static string ResolveGlobalLobbyName(
        string provider,
        string externalId,
        IReadOnlyDictionary<(string Provider, string ExternalId), string> names,
        string currentProvider,
        string currentExternalId,
        string? currentDisplayName)
    {
        if (string.Equals(provider, currentProvider, StringComparison.Ordinal) &&
            string.Equals(externalId, currentExternalId, StringComparison.Ordinal) &&
            !string.IsNullOrWhiteSpace(currentDisplayName))
        {
            return currentDisplayName;
        }

        return names.TryGetValue((provider, externalId), out var displayName) &&
               !string.IsNullOrWhiteSpace(displayName)
            ? displayName
            : externalId;
    }

    private static bool IsGlobalLobbySameIdentity(
        string leftProvider,
        string leftExternalId,
        string rightProvider,
        string rightExternalId)
        => string.Equals(leftProvider, rightProvider, StringComparison.Ordinal) &&
           string.Equals(leftExternalId, rightExternalId, StringComparison.Ordinal);

    private void AddGlobalLobbyEmptyState(Panel panel, string text)
    {
        var empty = new Border
        {
            Padding = new Thickness(24),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12)
        };
        empty.SetResourceReference(Border.BackgroundProperty, "PanelBrush");
        empty.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");
        var message = new TextBlock
        {
            Text = text,
            FontSize = 14,
            TextWrapping = TextWrapping.Wrap
        };
        message.SetResourceReference(TextBlock.ForegroundProperty, "MutedTextBrush");
        empty.Child = message;
        panel.Children.Add(empty);
    }

    private void GlobalLobbyOpenWorldButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: UnifiedWorldListItem item })
        {
            return;
        }

        HideGlobalLobby(showGames: false);
        OpenGameWorkspace(item.AdapterId, item.GameName, item.World.Id);
    }

    private void AddBuildIdentityToMoreMenu()
    {
        if (_moreButton?.ContextMenu is not { } menu ||
            menu.Items.OfType<MenuItem>().Any(item => Equals(item.Tag, "build-identity")))
        {
            return;
        }

        menu.Items.Add(new Separator());
        menu.Items.Add(new MenuItem
        {
            Header = $"Version {StewardBuildVersion.Current}",
            IsEnabled = false,
            Tag = "build-identity"
        });
    }
}
