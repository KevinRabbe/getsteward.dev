using System.Collections;
using System.ComponentModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;

namespace SharedWorlds.Desktop;

public partial class MainWindow
{
    private bool _safeWorldGamesHomeInitialized;
    private bool _safeWorldGameFilterApplying;
    private object? _safeWorldFilteredItemsSource;

    internal async Task InitializeSafeWorldGamesHomeAsync()
    {
        if (_safeWorldGamesHomeInitialized)
        {
            return;
        }

        _safeWorldGamesHomeInitialized = true;

        var itemsSourceDescriptor = DependencyPropertyDescriptor.FromProperty(
            ItemsControl.ItemsSourceProperty,
            typeof(ListBox));
        itemsSourceDescriptor?.AddValueChanged(
            GameLibraryList,
            (_, _) => QueueSafeWorldGameLibraryFilter());

        var selectedGameDescriptor = DependencyPropertyDescriptor.FromProperty(
            TextBlock.TextProperty,
            typeof(TextBlock));
        selectedGameDescriptor?.AddValueChanged(
            SelectedGameNameText,
            (_, _) => UpdateNativeWorldCreationActionState());

        GameLibraryList.SelectionChanged += SafeWorldGameLibraryList_SelectionChanged;

        RehomeSafeWorldGameActions();
        RebuildSafeWorldSelectedGameSidebar();

        // The import browser remains the existing authoritative discovery/capture flow, but once a
        // game is selected the game itself is already the scope. Do not ask the user to choose it twice.
        if (_importGameTiles?.Parent is ScrollViewer importGameScroller)
        {
            importGameScroller.Visibility = Visibility.Collapsed;
        }

        ApplySafeWorldAddWorldCopy();
        await ApplySafeWorldGameLibraryFilterAsync();
    }

    private void RehomeSafeWorldGameActions()
    {
        if (OpenImportButton.Parent is not Grid gamesHeader ||
            BackToGamesButton.Parent is not DockPanel gameHeader)
        {
            return;
        }

        // The Games home is product navigation, not a command bar. Keep its title/description visible,
        // but move mutations into the selected-game workspace and keep refresh as an internal signal.
        gamesHeader.Children.Remove(OpenImportButton);
        if (_createWorldButton?.Parent == gamesHeader)
        {
            gamesHeader.Children.Remove(_createWorldButton);
        }

        RefreshButton.Visibility = Visibility.Collapsed;

        if (RefreshGameButton.Parent == gameHeader)
        {
            gameHeader.Children.Remove(RefreshGameButton);
        }
        RefreshGameButton.Visibility = Visibility.Collapsed;

        Grid.SetColumn(OpenImportButton, 0);
        DockPanel.SetDock(OpenImportButton, Dock.Right);
        OpenImportButton.Margin = new Thickness(8, 0, 0, 0);
        OpenImportButton.MinHeight = 40;
        AutomationProperties.SetHelpText(
            OpenImportButton,
            "Add a World already saved by this game to Safe World.");
        gameHeader.Children.Add(OpenImportButton);

        if (_createWorldButton is not null)
        {
            _createWorldButton.Content = DesktopText.CreateWorld;
            _createWorldButton.Margin = new Thickness(8, 0, 0, 0);
            _createWorldButton.MinHeight = 40;
            DockPanel.SetDock(_createWorldButton, Dock.Right);
            AutomationProperties.SetHelpText(
                _createWorldButton,
                "Create a new World for this game.");
            gameHeader.Children.Add(_createWorldButton);
        }

        UpdateNativeWorldCreationActionState();
    }

    private void RebuildSafeWorldSelectedGameSidebar()
    {
        // Rebuild the selected-game navigation from the controls that actually belong to this scope.
        // Back is navigation; game identity is the hierarchy; Create/Add are contextual actions.
        DetachSafeWorldSidebarElement(BackToGamesButton);
        DetachSafeWorldSidebarElement(OpenImportButton);
        if (_createWorldButton is not null)
        {
            DetachSafeWorldSidebarElement(_createWorldButton);
        }
        DetachSafeWorldSidebarElement(SelectedGameNameText);
        DetachSafeWorldSidebarElement(_worldSearchBox);
        DetachSafeWorldSidebarElement(WorldList);

        var layout = new Grid();
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var header = new StackPanel
        {
            Margin = new Thickness(0, 0, 0, 18)
        };

        BackToGamesButton.HorizontalAlignment = HorizontalAlignment.Left;
        BackToGamesButton.Width = double.NaN;
        BackToGamesButton.Height = 36;
        BackToGamesButton.MinWidth = 0;
        BackToGamesButton.MinHeight = 0;
        BackToGamesButton.Padding = new Thickness(8, 5, 8, 5);
        BackToGamesButton.Margin = new Thickness(-8, 0, 0, 14);
        if (TryFindResource("GhostButtonStyle") is Style ghostButtonStyle)
        {
            BackToGamesButton.Style = ghostButtonStyle;
        }
        header.Children.Add(BackToGamesButton);

        SelectedGameNameText.Margin = new Thickness(0);
        SelectedGameNameText.FontSize = 26;
        SelectedGameNameText.FontWeight = FontWeights.Bold;
        SelectedGameNameText.TextWrapping = TextWrapping.Wrap;
        header.Children.Add(SelectedGameNameText);

        var sectionLabel = new TextBlock
        {
            Text = DesktopText.Worlds.ToUpperInvariant(),
            Margin = new Thickness(0, 6, 0, 14),
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
            Foreground = (Brush)FindResource("MutedTextBrush")
        };
        header.Children.Add(sectionLabel);

        var actions = new WrapPanel();
        if (_createWorldButton is not null)
        {
            _createWorldButton.Width = 166;
            _createWorldButton.Height = 40;
            _createWorldButton.MinWidth = 0;
            _createWorldButton.MinHeight = 0;
            _createWorldButton.Margin = new Thickness(0, 0, 10, 8);
            actions.Children.Add(_createWorldButton);
        }

        OpenImportButton.Width = 166;
        OpenImportButton.Height = 40;
        OpenImportButton.MinWidth = 0;
        OpenImportButton.MinHeight = 0;
        OpenImportButton.Margin = new Thickness(0, 0, 0, 8);
        if (TryFindResource("GhostButtonStyle") is Style addWorldStyle)
        {
            OpenImportButton.Style = addWorldStyle;
        }
        actions.Children.Add(OpenImportButton);
        header.Children.Add(actions);

        Grid.SetRow(header, 0);
        layout.Children.Add(header);

        _worldSearchBox.Margin = new Thickness(0, 0, 0, 14);
        _worldSearchBox.MinHeight = 42;
        _worldSearchBox.HorizontalAlignment = HorizontalAlignment.Stretch;
        _worldSearchBox.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetRow(_worldSearchBox, 1);
        layout.Children.Add(_worldSearchBox);

        WorldList.Margin = new Thickness(0);
        WorldList.HorizontalContentAlignment = HorizontalAlignment.Stretch;
        ScrollViewer.SetHorizontalScrollBarVisibility(WorldList, ScrollBarVisibility.Disabled);
        ScrollViewer.SetVerticalScrollBarVisibility(WorldList, ScrollBarVisibility.Auto);
        Grid.SetRow(WorldList, 2);
        layout.Children.Add(WorldList);

        WorldSidebar.Padding = new Thickness(22, 20, 18, 20);
        WorldSidebar.Child = layout;
    }

    private static void DetachSafeWorldSidebarElement(FrameworkElement element)
    {
        switch (element.Parent)
        {
            case Panel panel:
                panel.Children.Remove(element);
                break;
            case Decorator decorator when ReferenceEquals(decorator.Child, element):
                decorator.Child = null;
                break;
            case ContentControl contentControl when ReferenceEquals(contentControl.Content, element):
                contentControl.Content = null;
                break;
        }
    }

    private void SafeWorldGameLibraryList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (GameLibraryList.SelectedItem is not GameLibraryItem selected)
        {
            return;
        }

        // Existing import discovery already owns native World truth. Keep its selected game aligned
        // with the game-first shell instead of creating a second discovery model.
        _selectedManagedGameId = selected.AdapterId;
        _selectedImportGameId = selected.AdapterId;
        UpdateNativeWorldCreationActionState();
    }

    private void QueueSafeWorldGameLibraryFilter()
    {
        if (_safeWorldGameFilterApplying ||
            ReferenceEquals(GameLibraryList.ItemsSource, _safeWorldFilteredItemsSource))
        {
            return;
        }

        _ = Dispatcher.BeginInvoke(
            new Action(async () => await ApplySafeWorldGameLibraryFilterAsync()));
    }

    private async Task ApplySafeWorldGameLibraryFilterAsync()
    {
        if (_safeWorldGameFilterApplying ||
            ReferenceEquals(GameLibraryList.ItemsSource, _safeWorldFilteredItemsSource) ||
            GameLibraryList.ItemsSource is not IEnumerable source)
        {
            return;
        }

        var games = source.Cast<object>().OfType<GameLibraryItem>().ToList();
        _safeWorldGameFilterApplying = true;
        try
        {
            var visible = new List<GameLibraryItem>(games.Count);
            foreach (var game in games)
            {
                var hasSafeWorld = _allWorldItems.Any(item =>
                    string.Equals(item.AdapterId, game.AdapterId, StringComparison.Ordinal));
                if (hasSafeWorld)
                {
                    visible.Add(game);
                    continue;
                }

                if (!_registeredGameAdapters.TryGetValue(game.AdapterId, out var adapter))
                {
                    continue;
                }

                try
                {
                    if ((await adapter.DiscoverInstallationsAsync()).Count > 0)
                    {
                        visible.Add(game);
                    }
                }
                catch
                {
                    // This is presentation filtering, not an authority boundary. If installation
                    // discovery unexpectedly fails after the ordinary library refresh, fail open so
                    // Safe World never hides an existing game merely because this cosmetic pass failed.
                    visible.Add(game);
                }
            }

            _safeWorldFilteredItemsSource = visible;
            GameLibraryList.ItemsSource = visible;
        }
        finally
        {
            _safeWorldGameFilterApplying = false;
        }
    }

    private void ApplySafeWorldAddWorldCopy()
    {
        if (_importBrowserImportButton is not null)
        {
            _importBrowserImportButton.Content = "Add to Safe World";
            _importBrowserImportButton.MinWidth = 150;
        }
    }
}
