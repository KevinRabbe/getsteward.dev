using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;

namespace SharedWorlds.Desktop;

public partial class MainWindow
{
    private const string LobbyCardVisualDesignTag = "safe-world-v2-lobby-card";

    private Button? _gamesNavigationButton;
    private bool _visualDesignV2Initialized;

    internal void InitializeVisualDesignV2()
    {
        if (_visualDesignV2Initialized)
        {
            return;
        }

        _visualDesignV2Initialized = true;
        InitializePrimaryNavigationV2();
        ApplySelectedGameVisualHierarchyV2();
        ApplyWorldActionHierarchyV2();
        ApplyGlobalSurfaceDesignV2();

        if (_globalLobbyRows is not null)
        {
            _globalLobbyRows.LayoutUpdated += (_, _) => ApplyLobbyCardVisualDesignV2();
        }

        GamesLibraryPanel.IsVisibleChanged += (_, _) => UpdatePrimaryNavigationVisualStateV2();
        if (_globalLobbyPanel is not null)
        {
            _globalLobbyPanel.IsVisibleChanged += (_, _) => UpdatePrimaryNavigationVisualStateV2();
        }
        if (_globalSettingsPanel is not null)
        {
            _globalSettingsPanel.IsVisibleChanged += (_, _) => UpdatePrimaryNavigationVisualStateV2();
        }
        RefreshButton.IsEnabledChanged += (_, _) => UpdatePrimaryNavigationVisualStateV2();

        UpdatePrimaryNavigationVisualStateV2();
    }

    private void InitializePrimaryNavigationV2()
    {
        if (_globalLobbyButton?.Parent is not StackPanel navigation ||
            _gamesNavigationButton is not null)
        {
            return;
        }

        var gamesButton = new Button
        {
            Content = DesktopText.Games,
            MinWidth = 82,
            Height = 38,
            Margin = new Thickness(0, 0, 4, 0)
        };
        AutomationProperties.SetName(gamesButton, DesktopText.Games);
        AutomationProperties.SetHelpText(gamesButton, "Open your Games Library.");
        gamesButton.Click += (_, _) => ShowGamesFromPrimaryNavigationV2();
        navigation.Children.Insert(0, gamesButton);
        _gamesNavigationButton = gamesButton;

        ConfigureTopNavigationButtonV2(_globalLobbyButton, minWidth: 82, rightMargin: 4);
        if (_moreButton is not null)
        {
            ConfigureTopNavigationButtonV2(_moreButton, minWidth: 76, rightMargin: 4);
        }
        if (_globalSettingsButton is not null)
        {
            ConfigureTopNavigationButtonV2(_globalSettingsButton, minWidth: 90, rightMargin: 0);
        }
    }

    private void ConfigureTopNavigationButtonV2(Button button, double minWidth, double rightMargin)
    {
        button.Width = double.NaN;
        button.Height = 38;
        button.MinWidth = minWidth;
        button.MinHeight = 0;
        button.Padding = new Thickness(14, 7, 14, 7);
        button.Margin = new Thickness(0, 0, rightMargin, 0);
        if (TryFindResource("TopNavButtonStyle") is Style style)
        {
            button.Style = style;
        }
    }

    private void ShowGamesFromPrimaryNavigationV2()
    {
        if (_globalLobbyVisible)
        {
            HideGlobalLobby(showGames: true);
        }
        else
        {
            if (_globalSettingsVisible)
            {
                HideGlobalSettings();
            }
            ShowGamesLibrary(focusLibrary: true);
        }

        UpdatePrimaryNavigationVisualStateV2();
    }

    private void UpdatePrimaryNavigationVisualStateV2()
    {
        if (_gamesNavigationButton is null)
        {
            return;
        }

        var activeStyle = TryFindResource("TopNavActiveButtonStyle") as Style;
        var idleStyle = TryFindResource("TopNavButtonStyle") as Style;
        if (activeStyle is null || idleStyle is null)
        {
            return;
        }

        _gamesNavigationButton.Style = !_globalLobbyVisible && !_globalSettingsVisible
            ? activeStyle
            : idleStyle;
        if (_globalLobbyButton is not null)
        {
            _globalLobbyButton.Style = _globalLobbyVisible ? activeStyle : idleStyle;
            _globalLobbyButton.IsEnabled = RefreshButton.IsEnabled;
        }
        if (_globalSettingsButton is not null)
        {
            _globalSettingsButton.Style = _globalSettingsVisible ? activeStyle : idleStyle;
            _globalSettingsButton.IsEnabled = RefreshButton.IsEnabled;
        }
        if (_moreButton is not null)
        {
            _moreButton.Style = idleStyle;
        }

        _gamesNavigationButton.IsEnabled = RefreshButton.IsEnabled;
    }

    private void ApplySelectedGameVisualHierarchyV2()
    {
        if (TryFindResource("GhostButtonStyle") is Style ghostStyle)
        {
            BackToGamesButton.Style = ghostStyle;
            OpenImportButton.Style = ghostStyle;
        }

        BackToGamesButton.Width = double.NaN;
        BackToGamesButton.MinWidth = 0;
        BackToGamesButton.Height = 36;

        if (_createWorldButton is not null)
        {
            _createWorldButton.Width = 166;
            _createWorldButton.Height = 40;
            _createWorldButton.MinWidth = 0;
            _createWorldButton.MinHeight = 0;
        }

        OpenImportButton.Width = 166;
        OpenImportButton.Height = 40;
        OpenImportButton.MinWidth = 0;
        OpenImportButton.MinHeight = 0;
    }

    private void ApplyWorldActionHierarchyV2()
    {
        var ghostStyle = TryFindResource("GhostButtonStyle") as Style;
        var primaryStyle = TryFindResource("PrimaryButtonStyle") as Style;
        var dangerStyle = TryFindResource("DangerButtonStyle") as Style;

        ConfigureWorldActionV2(ContinueButton, minWidth: 124, primaryStyle);
        ConfigureWorldActionV2(HostButton, minWidth: 112, style: null);
        ConfigureWorldActionV2(StopHostingButton, minWidth: 128, style: null);
        ConfigureWorldActionV2(ShareButton, minWidth: 104, ghostStyle);
        ConfigureWorldActionV2(VerifyEnvironmentButton, minWidth: 108, style: null, height: 40);
        ConfigureWorldActionV2(RepairEnvironmentButton, minWidth: 100, ghostStyle, height: 40);

        if (_joinButton is not null)
        {
            ConfigureWorldActionV2(_joinButton, minWidth: 112, primaryStyle);
        }
        if (_shareCopyButton is not null)
        {
            ConfigureWorldActionV2(_shareCopyButton, minWidth: 104, ghostStyle);
        }
        if (_deleteWorldButton is not null)
        {
            ConfigureWorldActionV2(_deleteWorldButton, minWidth: 118, dangerStyle, height: 40);
        }
    }

    private static void ConfigureWorldActionV2(
        Button button,
        double minWidth,
        Style? style,
        double height = 42)
    {
        button.Width = double.NaN;
        button.MinWidth = minWidth;
        button.Height = height;
        button.MinHeight = 0;
        button.Padding = new Thickness(16, 8, 16, 8);
        if (style is not null)
        {
            button.Style = style;
        }
    }

    private void ApplyGlobalSurfaceDesignV2()
    {
        ApplyLobbySurfaceDesignV2();
        ApplySettingsSurfaceDesignV2();
    }

    private void ApplyLobbySurfaceDesignV2()
    {
        if (_globalLobbyPanel?.Child is not ScrollViewer scroll ||
            scroll.Content is not StackPanel content)
        {
            return;
        }

        _globalLobbyPanel.Padding = new Thickness(40, 34, 40, 44);
        content.MaxWidth = 1040;
        content.HorizontalAlignment = HorizontalAlignment.Center;

        var backButton = content.Children.OfType<Button>().FirstOrDefault();
        if (backButton is not null)
        {
            backButton.Visibility = Visibility.Collapsed;
        }

        var lobbyTitle = content.Children
            .OfType<TextBlock>()
            .FirstOrDefault(text => string.Equals(text.Text, DesktopText.Lobby, StringComparison.Ordinal));
        if (lobbyTitle is not null)
        {
            lobbyTitle.FontSize = 34;
            lobbyTitle.FontWeight = FontWeights.Bold;
        }

        var description = content.Children
            .OfType<TextBlock>()
            .FirstOrDefault(text => string.Equals(text.Text, DesktopText.LobbyDescription, StringComparison.Ordinal));
        if (description is not null)
        {
            description.FontSize = 14;
            description.MaxWidth = 680;
            description.Margin = new Thickness(0, 7, 0, 26);
        }

        if (_invitationsButton is not null)
        {
            _invitationsButton.Width = double.NaN;
            _invitationsButton.MinWidth = 104;
            _invitationsButton.Height = 38;
            if (TryFindResource("GhostButtonStyle") is Style ghostStyle)
            {
                _invitationsButton.Style = ghostStyle;
            }
        }
    }

    private void ApplySettingsSurfaceDesignV2()
    {
        if (_globalSettingsPanel?.Child is not ScrollViewer scroll ||
            scroll.Content is not StackPanel content)
        {
            return;
        }

        _globalSettingsPanel.Padding = new Thickness(40, 34, 40, 44);
        content.MaxWidth = 900;
        content.HorizontalAlignment = HorizontalAlignment.Center;

        var backButton = content.Children.OfType<Button>().FirstOrDefault();
        if (backButton is not null)
        {
            backButton.Visibility = Visibility.Collapsed;
        }

        var title = content.Children
            .OfType<TextBlock>()
            .FirstOrDefault(text => string.Equals(text.Text, DesktopText.Settings, StringComparison.Ordinal));
        if (title is not null)
        {
            title.FontSize = 34;
            title.FontWeight = FontWeights.Bold;
        }

        var description = content.Children
            .OfType<TextBlock>()
            .FirstOrDefault(text => string.Equals(text.Text, DesktopText.SettingsDescription, StringComparison.Ordinal));
        if (description is not null)
        {
            description.FontSize = 14;
            description.MaxWidth = 680;
            description.Margin = new Thickness(0, 7, 0, 26);
        }

        foreach (var expander in content.Children.OfType<Expander>())
        {
            expander.FontSize = 15;
            expander.FontWeight = FontWeights.SemiBold;
        }
    }

    private void ApplyLobbyCardVisualDesignV2()
    {
        if (_globalLobbyRows is null)
        {
            return;
        }

        foreach (var card in _globalLobbyRows.Children.OfType<Border>())
        {
            if (Equals(card.Tag, LobbyCardVisualDesignTag))
            {
                continue;
            }

            card.Tag = LobbyCardVisualDesignTag;
            card.Padding = new Thickness(22);
            card.Margin = new Thickness(0, 0, 0, 14);
            card.CornerRadius = new CornerRadius(16);
            card.SetResourceReference(Border.BackgroundProperty, "PanelBrush");
            card.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");

            if (card.Child is not StackPanel root)
            {
                continue;
            }

            RestyleLobbyCardHeaderV2(root);
            RestyleLobbyCardTextV2(root);
        }
    }

    private void RestyleLobbyCardHeaderV2(StackPanel root)
    {
        var header = root.Children.OfType<DockPanel>().FirstOrDefault();
        if (header is null)
        {
            return;
        }

        var openButton = header.Children.OfType<Button>().FirstOrDefault();
        var title = header.Children.OfType<StackPanel>().FirstOrDefault();
        if (openButton?.Tag is not UnifiedWorldListItem item || title is null)
        {
            return;
        }

        header.Children.Remove(openButton);
        header.Children.Remove(title);

        var composedHeader = new Grid();
        composedHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        composedHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        composedHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var iconShell = new Border
        {
            Width = 52,
            Height = 52,
            Margin = new Thickness(0, 0, 14, 0),
            CornerRadius = new CornerRadius(12),
            BorderThickness = new Thickness(1)
        };
        iconShell.SetResourceReference(Border.BackgroundProperty, "PanelAltBrush");
        iconShell.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");
        var icon = new Image
        {
            Width = 42,
            Height = 42,
            Stretch = Stretch.Uniform,
            DataContext = item
        };
        icon.SetBinding(Image.SourceProperty, new Binding(nameof(UnifiedWorldListItem.GameIconPath)));
        iconShell.Child = icon;
        Grid.SetColumn(iconShell, 0);
        composedHeader.Children.Add(iconShell);

        title.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(title, 1);
        composedHeader.Children.Add(title);

        openButton.Content = $"{DesktopText.OpenWorld}  →";
        openButton.Width = double.NaN;
        openButton.MinWidth = 100;
        openButton.Height = 38;
        openButton.Margin = new Thickness(16, 0, 0, 0);
        if (TryFindResource("GhostButtonStyle") is Style ghostStyle)
        {
            openButton.Style = ghostStyle;
        }
        Grid.SetColumn(openButton, 2);
        composedHeader.Children.Add(openButton);

        var headerIndex = root.Children.IndexOf(header);
        root.Children.Remove(header);
        root.Children.Insert(headerIndex, composedHeader);
    }

    private void RestyleLobbyCardTextV2(StackPanel root)
    {
        var textBlocks = root.Children.OfType<TextBlock>().ToList();
        for (var index = 0; index < textBlocks.Count; index++)
        {
            var text = textBlocks[index];
            if (string.Equals(text.Text, DesktopText.PlayingNow, StringComparison.Ordinal) ||
                string.Equals(text.Text, DesktopText.WorldGroup, StringComparison.Ordinal))
            {
                text.FontSize = 11;
                text.FontWeight = FontWeights.SemiBold;
                text.Margin = new Thickness(0, 18, 0, 6);
                text.SetResourceReference(TextBlock.ForegroundProperty, "MutedTextBrush");
            }
            else
            {
                text.FontSize = 13;
                text.LineHeight = 20;
            }
        }

        var playingHeading = root.Children
            .OfType<TextBlock>()
            .FirstOrDefault(text => string.Equals(text.Text, DesktopText.PlayingNow, StringComparison.Ordinal));
        if (playingHeading is null)
        {
            return;
        }

        var headingIndex = root.Children.IndexOf(playingHeading);
        if (headingIndex + 1 >= root.Children.Count ||
            root.Children[headingIndex + 1] is not TextBlock playingValue)
        {
            return;
        }

        if (!playingValue.Text.StartsWith("No one", StringComparison.OrdinalIgnoreCase))
        {
            playingValue.SetResourceReference(TextBlock.ForegroundProperty, "SuccessBrush");
            playingValue.FontWeight = FontWeights.SemiBold;
        }
    }
}
