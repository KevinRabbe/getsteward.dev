using System.ComponentModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;

namespace SharedWorlds.Desktop;

public partial class MainWindow
{
    private readonly TextBox _worldSearchBox = new()
    {
        MinHeight = 34,
        Margin = new Thickness(2, 0, 2, 8),
        Padding = new Thickness(10, 5, 10, 5),
        VerticalContentAlignment = VerticalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Top,
        ToolTip = "Search managed Worlds by World name or game."
    };
    private DependencyPropertyDescriptor? _worldItemsSourceDescriptor;
    private bool _worldSearchUiInitialized;

    internal void InitializeWorldSearchUi()
    {
        if (_worldSearchUiInitialized)
        {
            return;
        }

        _worldSearchUiInitialized = true;
        AutomationProperties.SetName(_worldSearchBox, "Search Worlds");
        _worldSearchBox.SetResourceReference(Control.BackgroundProperty, "PanelAltBrush");
        _worldSearchBox.SetResourceReference(Control.ForegroundProperty, "TextBrush");
        _worldSearchBox.SetResourceReference(Control.BorderBrushProperty, "BorderBrush");

        if (WorldList.Parent is Grid worldSidebarGrid)
        {
            Grid.SetRow(_worldSearchBox, Grid.GetRow(WorldList));
            Panel.SetZIndex(_worldSearchBox, 1);
            worldSidebarGrid.Children.Add(_worldSearchBox);

            var margin = WorldList.Margin;
            WorldList.Margin = new Thickness(
                margin.Left,
                margin.Top + 42,
                margin.Right,
                margin.Bottom);
        }

        _worldSearchBox.TextChanged += (_, _) => ApplyWorldSearchFilter();
        _worldItemsSourceDescriptor = DependencyPropertyDescriptor.FromProperty(
            ItemsControl.ItemsSourceProperty,
            typeof(ListBox));
        _worldItemsSourceDescriptor?.AddValueChanged(
            WorldList,
            (_, _) => ApplyWorldSearchFilter());

        ApplyWorldSearchFilter();
    }

    private void ApplyWorldSearchFilter()
    {
        if (!_worldSearchUiInitialized || WorldList.ItemsSource is null)
        {
            return;
        }

        var view = CollectionViewSource.GetDefaultView(WorldList.ItemsSource);
        if (view is null)
        {
            return;
        }

        view.Filter = MatchesWorldSearch;
        view.Refresh();

        if (WorldList.SelectedItem is not null && view.Contains(WorldList.SelectedItem))
        {
            return;
        }

        WorldList.SelectedItem = view.Cast<object>().FirstOrDefault();
        if (WorldList.SelectedItem is null && !string.IsNullOrWhiteSpace(_worldSearchBox.Text))
        {
            _selectedWorld = null;
            EmptyStateText.Text = "No managed Worlds match this search.";
            EmptyStateText.Visibility = Visibility.Visible;
            WorldDetailsPanel.Visibility = Visibility.Collapsed;
            UpdateUnifiedActionState();
            UpdateResponsibilityPresentation();
        }
    }

    private bool MatchesWorldSearch(object item)
    {
        if (item is not UnifiedWorldListItem world)
        {
            return false;
        }

        var query = _worldSearchBox.Text.Trim();
        if (query.Length == 0)
        {
            return true;
        }

        return world.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
               world.GameName.Contains(query, StringComparison.OrdinalIgnoreCase);
    }
}
