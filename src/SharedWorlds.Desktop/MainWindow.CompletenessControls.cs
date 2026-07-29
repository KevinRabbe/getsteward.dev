using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;

namespace SharedWorlds.Desktop;

public partial class MainWindow
{
    private Button? _globalSettingsButton;
    private Border? _globalSettingsPanel;
    private bool _globalSettingsVisible;

    private void InitializeCompletenessControlsUi()
    {
        // Search already has one owner in MainWindow.WorldSearch.cs. Reuse that implementation rather
        // than creating another search box/filter lifecycle just to satisfy the game-workspace contract.
        InitializeWorldSearchUi();
        InitializeGlobalSettingsSurface();
    }

    private void InitializeGlobalSettingsSurface()
    {
        if (_globalSettingsPanel is not null || WorkspaceGrid.Parent is not Grid root)
        {
            return;
        }

        var headerBorder = root.Children
            .OfType<Border>()
            .FirstOrDefault(child => Grid.GetRow(child) == 0);
        if (headerBorder?.Child is not DockPanel header)
        {
            return;
        }

        var settingsButton = new Button
        {
            Content = DesktopText.Settings,
            Padding = new Thickness(12, 6, 12, 6),
            MinHeight = 32,
            VerticalAlignment = VerticalAlignment.Center
        };
        DockPanel.SetDock(settingsButton, Dock.Right);
        AutomationProperties.SetName(settingsButton, DesktopText.Settings);
        AutomationProperties.SetHelpText(settingsButton, "Open Safe World settings for this device.");
        header.Children.Add(settingsButton);

        var settingsPanel = new Border
        {
            Visibility = Visibility.Collapsed,
            Padding = new Thickness(32, 28, 32, 32),
            Focusable = true
        };
        settingsPanel.SetResourceReference(Border.BackgroundProperty, "AppBackgroundBrush");
        Grid.SetColumn(settingsPanel, 0);
        Grid.SetColumnSpan(settingsPanel, 2);
        Panel.SetZIndex(settingsPanel, 100);

        var scroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        };
        var content = new StackPanel
        {
            MaxWidth = 760,
            HorizontalAlignment = HorizontalAlignment.Left
        };

        var backButton = new Button
        {
            Content = DesktopText.BackToGames,
            Width = 112,
            Height = 40,
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 0, 0, 20)
        };
        AutomationProperties.SetName(backButton, DesktopText.BackToGames);
        content.Children.Add(backButton);
        content.Children.Add(new TextBlock
        {
            Text = DesktopText.Settings,
            FontSize = 30,
            FontWeight = FontWeights.SemiBold
        });

        var description = new TextBlock
        {
            Text = DesktopText.SettingsDescription,
            Margin = new Thickness(0, 6, 0, 22),
            TextWrapping = TextWrapping.Wrap,
            FontSize = 13
        };
        description.SetResourceReference(TextBlock.ForegroundProperty, "MutedTextBrush");
        content.Children.Add(description);

        // Re-home the existing settings control instead of duplicating its persistence or state logic.
        // AllowHostingCheckBox and HostingPreferenceText remain the same named WPF instances used by
        // device-settings load/save and hosting action-state code.
        if (WorldSidebar.Child is Grid sidebarGrid)
        {
            var existingSettings = sidebarGrid.Children
                .OfType<Expander>()
                .FirstOrDefault(child => Grid.GetRow(child) == 2);
            if (existingSettings is not null)
            {
                sidebarGrid.Children.Remove(existingSettings);
                existingSettings.Header = DesktopText.HostingOnThisDevice;
                existingSettings.IsExpanded = true;
                existingSettings.Margin = new Thickness(0);
                content.Children.Add(existingSettings);
            }
        }

        scroll.Content = content;
        settingsPanel.Child = scroll;
        WorkspaceGrid.Children.Add(settingsPanel);

        _globalSettingsButton = settingsButton;
        _globalSettingsPanel = settingsPanel;

        settingsButton.Click += (_, _) => ShowGlobalSettings();
        backButton.Click += (_, _) => HideGlobalSettings(showGames: true);
        RefreshButton.IsEnabledChanged += (_, _) => UpdateGlobalSettingsButtonState();
    }

    private void ShowGlobalSettings()
    {
        if (_globalSettingsPanel is null)
        {
            return;
        }

        if (_globalLobbyVisible)
        {
            HideGlobalLobby(showGames: false);
        }

        _globalSettingsVisible = true;
        _globalSettingsPanel.Visibility = Visibility.Visible;
        GamesLibraryPanel.IsEnabled = false;
        WorldSidebar.IsEnabled = false;
        WorldDetailsScroll.IsEnabled = false;
        UpdateGlobalSettingsButtonState();
        _globalSettingsPanel.Focus();
    }

    private void HideGlobalSettings(bool showGames = false)
    {
        if (_globalSettingsPanel is null)
        {
            return;
        }

        _globalSettingsVisible = false;
        _globalSettingsPanel.Visibility = Visibility.Collapsed;
        GamesLibraryPanel.IsEnabled = true;
        WorldSidebar.IsEnabled = true;
        WorldDetailsScroll.IsEnabled = true;
        UpdateGlobalSettingsButtonState();

        if (showGames)
        {
            ShowGamesLibrary(focusLibrary: true);
        }
        else
        {
            ApplyGameNavigationLayout();
        }
    }

    private void UpdateGlobalSettingsButtonState()
    {
        if (_globalSettingsButton is not null)
        {
            // Settings is now a top-level navigation tab. Keep the selected tab interactive/legible;
            // busy state still disables navigation through the same existing RefreshButton signal.
            _globalSettingsButton.IsEnabled = RefreshButton.IsEnabled;
        }
    }
}
