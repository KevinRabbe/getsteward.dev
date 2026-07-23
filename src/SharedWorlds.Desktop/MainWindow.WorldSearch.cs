using System.ComponentModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;

namespace SharedWorlds.Desktop;

public partial class MainWindow
{
    private const string WorldSearchPlaceholder = "Search Worlds";

    private readonly TextBox _worldSearchBox = new()
    {
        MinHeight = 34,
        Margin = new Thickness(2, 0, 2, 8),
        Padding = new Thickness(10, 5, 10, 5),
        VerticalContentAlignment = VerticalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Top,
        ToolTip = "Search managed Worlds by World name or game. Ctrl+F focuses this search."
    };
    private DependencyPropertyDescriptor? _worldItemsSourceDescriptor;
    private bool _worldSearchUiInitialized;
    private bool _worldSearchShowingPlaceholder;

    internal void InitializeWorldSearchUi()
    {
        if (_worldSearchUiInitialized)
        {
            return;
        }

        _worldSearchUiInitialized = true;
        AutomationProperties.SetName(_worldSearchBox, WorldSearchPlaceholder);
        AutomationProperties.SetHelpText(
            _worldSearchBox,
            "Type a World or game name. Press Escape to clear the current search.");
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
        _worldSearchBox.GotKeyboardFocus += (_, _) => ClearWorldSearchPlaceholder();
        _worldSearchBox.LostKeyboardFocus += (_, _) => RestoreWorldSearchPlaceholder();
        PreviewKeyDown += WorldSearchPreviewKeyDown;
        _worldItemsSourceDescriptor = DependencyPropertyDescriptor.FromProperty(
            ItemsControl.ItemsSourceProperty,
            typeof(ListBox));
        _worldItemsSourceDescriptor?.AddValueChanged(
            WorldList,
            (_, _) => ApplyWorldSearchFilter());

        RestoreWorldSearchPlaceholder();
        ApplyWorldSearchFilter();
    }

    private void WorldSearchPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            _worldSearchBox.Focus();
            ClearWorldSearchPlaceholder();
            _worldSearchBox.SelectAll();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Escape &&
            _worldSearchBox.IsKeyboardFocusWithin &&
            GetWorldSearchQuery().Length > 0)
        {
            _worldSearchBox.Clear();
            e.Handled = true;
        }
    }

    private void ClearWorldSearchPlaceholder()
    {
        if (!_worldSearchShowingPlaceholder)
        {
            return;
        }

        _worldSearchShowingPlaceholder = false;
        _worldSearchBox.Text = string.Empty;
        _worldSearchBox.Opacity = 1;
    }

    private void RestoreWorldSearchPlaceholder()
    {
        if (_worldSearchShowingPlaceholder || !string.IsNullOrWhiteSpace(_worldSearchBox.Text))
        {
            return;
        }

        _worldSearchShowingPlaceholder = true;
        _worldSearchBox.Text = WorldSearchPlaceholder;
        _worldSearchBox.Opacity = 0.7;
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
        if (WorldList.SelectedItem is null && GetWorldSearchQuery().Length > 0)
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

        var query = GetWorldSearchQuery();
        if (query.Length == 0)
        {
            return true;
        }

        return world.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
               world.GameName.Contains(query, StringComparison.OrdinalIgnoreCase);
    }

    private string GetWorldSearchQuery()
        => _worldSearchShowingPlaceholder ? string.Empty : _worldSearchBox.Text.Trim();
}
