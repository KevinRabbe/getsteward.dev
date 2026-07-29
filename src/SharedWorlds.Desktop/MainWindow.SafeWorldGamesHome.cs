using System.Collections;
using System.ComponentModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;

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

        GameLibraryList.SelectionChanged += SafeWorldGameLibraryList_SelectionChanged;

        RehomeSafeWorldGameActions();

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

        // The games home is navigation, not a command bar. Actions belong to the selected game.
        gamesHeader.Children.Remove(OpenImportButton);
        if (_createWorldButton?.Parent == gamesHeader)
        {
            gamesHeader.Children.Remove(_createWorldButton);
        }

        gamesHeader.Visibility = Visibility.Collapsed;

        // Refresh remains an internal operation and is available from More, but it should not occupy
        // permanent selected-game chrome. The existing button object stays alive because busy-state
        // and legacy event wiring still use it as one internal presentation signal.
        if (RefreshGameButton.Parent == gameHeader)
        {
            gameHeader.Children.Remove(RefreshGameButton);
        }
        RefreshGameButton.Visibility = Visibility.Collapsed;

        Grid.SetColumn(OpenImportButton, 0);
        DockPanel.SetDock(OpenImportButton, Dock.Right);
        OpenImportButton.Margin = new Thickness(8, 0, 0, 0);
        OpenImportButton.MinHeight = 32;
        AutomationProperties.SetHelpText(
            OpenImportButton,
            "Add a World already saved by this game to Safe World.");
        gameHeader.Children.Add(OpenImportButton);

        if (_createWorldButton is not null)
        {
            _createWorldButton.Content = DesktopText.CreateWorld;
            _createWorldButton.Margin = new Thickness(8, 0, 0, 0);
            _createWorldButton.MinHeight = 32;
            DockPanel.SetDock(_createWorldButton, Dock.Right);
            AutomationProperties.SetHelpText(
                _createWorldButton,
                "Create a new World for this game.");
            gameHeader.Children.Add(_createWorldButton);
        }

        UpdateNativeWorldCreationActionState();
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

        if (_importWorkspace?.Child is not Grid layout)
        {
            return;
        }

        var header = layout.Children
            .OfType<DockPanel>()
            .FirstOrDefault(panel => Grid.GetRow(panel) == 0);
        var heading = header?.Children.OfType<StackPanel>().FirstOrDefault();
        if (heading?.Children.OfType<TextBlock>().ToArray() is { Length: >= 2 } headingText)
        {
            headingText[0].Text = "Add a World";
            headingText[1].Text = "Choose a World already saved by this game.";
        }

        var footer = layout.Children
            .OfType<DockPanel>()
            .FirstOrDefault(panel => Grid.GetRow(panel) == 4);
        var explanation = footer?.Children.OfType<TextBlock>().FirstOrDefault();
        if (explanation is not null)
        {
            explanation.Text =
                "Your original game save stays where it is. Safe World creates its own private copy.";
        }
    }
}
