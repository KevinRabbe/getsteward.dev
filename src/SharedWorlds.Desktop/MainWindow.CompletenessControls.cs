using System.ComponentModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;

namespace SharedWorlds.Desktop;

public partial class MainWindow
{
    private TextBox? _worldSearchBox;
    private ComboBox? _worldSortComboBox;
    private Button? _globalSettingsButton;
    private Border? _globalSettingsPanel;
    private bool _globalSettingsVisible;

    private void InitializeCompletenessControlsUi()
    {
        InitializeWorldWorkspaceSearchAndSort();
        InitializeGlobalSettingsSurface();
    }

    private void InitializeWorldWorkspaceSearchAndSort()
    {
        if (_worldSearchBox is not null || WorldSidebar.Child is not Grid sidebarGrid)
        {
            return;
        }

        var header = sidebarGrid.Children
            .OfType<StackPanel>()
            .FirstOrDefault(child => Grid.GetRow(child) == 0);
        if (header is null)
        {
            return;
        }

        var search = new TextBox
        {
            MinHeight = 32,
            Margin = new Thickness(0, 14, 0, 0),
            Padding = new Thickness(8, 5, 8, 5),
            ToolTip = "Filter Worlds in this game by name or visible summary."
        };
        AutomationProperties.SetName(search, "Search Worlds");
        AutomationProperties.SetHelpText(
            search,
            "Filter Worlds in the selected game by name, sharing state, or visible version text.");

        var sort = new ComboBox
        {
            MinHeight = 32,
            Margin = new Thickness(0, 8, 0, 0),
            ItemsSource = new[]
            {
                "Name A–Z",
                "Name Z–A"
            },
            SelectedIndex = 0
        };
        AutomationProperties.SetName(sort, "Sort Worlds");
        AutomationProperties.SetHelpText(sort, "Choose the name order for Worlds in this game.");

        header.Children.Add(search);
        header.Children.Add(sort);

        _worldSearchBox = search;
        _worldSortComboBox = sort;

        search.TextChanged += (_, _) => ApplyWorldWorkspaceSearchAndSort();
        sort.SelectionChanged += (_, _) => ApplyWorldWorkspaceSearchAndSort();

        var itemsSourceDescriptor = DependencyPropertyDescriptor.FromProperty(
            ItemsControl.ItemsSourceProperty,
            typeof(ListBox));
        itemsSourceDescriptor?.AddValueChanged(
            WorldList,
            (_, _) => ApplyWorldWorkspaceSearchAndSort());
    }

    private void ApplyWorldWorkspaceSearchAndSort()
    {
        if (_worldSearchBox is null ||
            _worldSortComboBox is null ||
            WorldList.ItemsSource is null)
        {
            return;
        }

        var query = _worldSearchBox.Text.Trim();
        WorldList.Items.Filter = item =>
        {
            if (item is not UnifiedWorldListItem world)
            {
                return false;
            }

            return query.Length == 0 ||
                   world.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                   world.Subtitle.Contains(query, StringComparison.OrdinalIgnoreCase);
        };

        if (WorldList.Items.CanSort)
        {
            using (WorldList.Items.DeferRefresh())
            {
                WorldList.Items.SortDescriptions.Clear();
                WorldList.Items.SortDescriptions.Add(new SortDescription(
                    nameof(UnifiedWorldListItem.Name),
                    _worldSortComboBox.SelectedIndex == 1
                        ? ListSortDirection.Descending
                        : ListSortDirection.Ascending));
            }
        }

        if (_selectedWorld is { } selectedWorld &&
            WorldList.Items
                .OfType<UnifiedWorldListItem>()
                .All(item => item.World.Id != selectedWorld.Id))
        {
            WorldList.SelectedItem = null;
        }

        if (WorldList.SelectedItem is null)
        {
            SetGameWorkspaceEmptyState();
            if (WorldList.Items.Count == 0 &&
                query.Length > 0 &&
                _selectedGameAdapterId is { } adapterId &&
                _allWorldItems.Any(item =>
                    string.Equals(item.AdapterId, adapterId, StringComparison.Ordinal)))
            {
                EmptyStateText.Text = $"No Worlds match ‘{query}’.";
            }
        }
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
            Content = "Settings",
            Padding = new Thickness(12, 6, 12, 6),
            MinHeight = 32,
            VerticalAlignment = VerticalAlignment.Center
        };
        DockPanel.SetDock(settingsButton, Dock.Right);
        AutomationProperties.SetName(settingsButton, "Settings");
        AutomationProperties.SetHelpText(settingsButton, "Open Steward settings for this device.");
        header.Children.Add(settingsButton);

        var settingsPanel = new Border
        {
            Visibility = Visibility.Collapsed,
            Padding = new Thickness(32, 28, 32, 32)
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
            Content = "← Back",
            Padding = new Thickness(10, 5, 10, 5),
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 0, 0, 18)
        };
        AutomationProperties.SetName(backButton, "Back from Settings");
        content.Children.Add(backButton);
        content.Children.Add(new TextBlock
        {
            Text = "Settings",
            FontSize = 30,
            FontWeight = FontWeights.SemiBold
        });

        var description = new TextBlock
        {
            Text = "Device-wide Steward preferences. World-specific rules remain with each World.",
            Margin = new Thickness(0, 6, 0, 22),
            TextWrapping = TextWrapping.Wrap,
            FontSize = 13
        };
        description.SetResourceReference(TextBlock.ForegroundProperty, "MutedTextBrush");
        content.Children.Add(description);

        // Re-home the existing settings control instead of duplicating its persistence or state logic.
        // AllowHostingCheckBox and HostingPreferenceText remain the same named WPF instances used by
        // device-settings load/save and busy/action-state code.
        if (WorldSidebar.Child is Grid sidebarGrid)
        {
            var existingSettings = sidebarGrid.Children
                .OfType<Expander>()
                .FirstOrDefault(child => Grid.GetRow(child) == 2);
            if (existingSettings is not null)
            {
                sidebarGrid.Children.Remove(existingSettings);
                existingSettings.Header = "Hosting on this device";
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
        backButton.Click += (_, _) => HideGlobalSettings();
        RefreshButton.IsEnabledChanged += (_, _) => UpdateGlobalSettingsButtonState();
    }

    private void ShowGlobalSettings()
    {
        if (_globalSettingsPanel is null)
        {
            return;
        }

        _globalSettingsVisible = true;
        _globalSettingsPanel.Visibility = Visibility.Visible;
        GamesLibraryPanel.IsEnabled = false;
        WorldSidebar.IsEnabled = false;
        WorldDetailsScroll.IsEnabled = false;
        UpdateGlobalSettingsButtonState();
        _globalSettingsPanel.Focus();
    }

    private void HideGlobalSettings()
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
        ApplyGameNavigationLayout();
        _globalSettingsButton?.Focus();
    }

    private void UpdateGlobalSettingsButtonState()
    {
        if (_globalSettingsButton is not null)
        {
            _globalSettingsButton.IsEnabled = !_globalSettingsVisible && RefreshButton.IsEnabled;
        }
    }
}
