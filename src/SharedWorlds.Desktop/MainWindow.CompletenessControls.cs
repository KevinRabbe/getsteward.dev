using System.ComponentModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;

namespace SharedWorlds.Desktop;

public partial class MainWindow
{
    private ComboBox? _worldSortComboBox;
    private DependencyPropertyDescriptor? _worldSortItemsSourceDescriptor;
    private Button? _globalSettingsButton;
    private Border? _globalSettingsPanel;
    private bool _globalSettingsVisible;

    private void InitializeCompletenessControlsUi()
    {
        // Search already has one owner in MainWindow.WorldSearch.cs. Reuse that implementation rather
        // than creating another search box/filter lifecycle just to satisfy the game-workspace contract.
        InitializeWorldSearchUi();
        InitializeWorldWorkspaceSort();
        InitializeGlobalSettingsSurface();
    }

    private void InitializeWorldWorkspaceSort()
    {
        if (_worldSortComboBox is not null || WorldList.Parent is not Grid worldSidebarGrid)
        {
            return;
        }

        var sort = new ComboBox
        {
            MinHeight = 34,
            Margin = new Thickness(2, 42, 2, 8),
            VerticalContentAlignment = VerticalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
            ItemsSource = new[]
            {
                "Name A–Z",
                "Name Z–A"
            },
            SelectedIndex = 0,
            ToolTip = "Sort managed Worlds in this game by name."
        };
        AutomationProperties.SetName(sort, "Sort Worlds");
        AutomationProperties.SetHelpText(sort, "Choose ascending or descending World-name order.");
        sort.SetResourceReference(Control.BackgroundProperty, "PanelAltBrush");
        sort.SetResourceReference(Control.ForegroundProperty, "TextBrush");
        sort.SetResourceReference(Control.BorderBrushProperty, "BorderBrush");

        Grid.SetRow(sort, Grid.GetRow(WorldList));
        Panel.SetZIndex(sort, 1);
        worldSidebarGrid.Children.Add(sort);

        // Existing search already reserves the first 42 px above the list. Reserve exactly one more
        // control row for sort instead of introducing a new sidebar layout/state model.
        var margin = WorldList.Margin;
        WorldList.Margin = new Thickness(
            margin.Left,
            margin.Top + 42,
            margin.Right,
            margin.Bottom);

        _worldSortComboBox = sort;
        sort.SelectionChanged += (_, _) => ApplyWorldSort();

        _worldSortItemsSourceDescriptor = DependencyPropertyDescriptor.FromProperty(
            ItemsControl.ItemsSourceProperty,
            typeof(ListBox));
        _worldSortItemsSourceDescriptor?.AddValueChanged(
            WorldList,
            (_, _) => ApplyWorldSort());

        ApplyWorldSort();
    }

    private void ApplyWorldSort()
    {
        if (_worldSortComboBox is null || WorldList.ItemsSource is null)
        {
            return;
        }

        var view = CollectionViewSource.GetDefaultView(WorldList.ItemsSource);
        if (view is null || !view.CanSort)
        {
            return;
        }

        using (view.DeferRefresh())
        {
            view.SortDescriptions.Clear();
            view.SortDescriptions.Add(new SortDescription(
                nameof(UnifiedWorldListItem.Name),
                _worldSortComboBox.SelectedIndex == 1
                    ? ListSortDirection.Descending
                    : ListSortDirection.Ascending));
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
        // device-settings load/save and hosting action-state code.
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
