using System.Collections;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using SharedWorlds.Core.Abstractions;
using SharedWorlds.Core.Domain;

namespace SharedWorlds.Desktop;

/// <summary>
/// Adds a game-first library and import experience on top of the generic adapter-driven UI.
/// Games are navigation; Worlds remain the products the user selects and plays.
/// </summary>
public partial class MainWindow
{
    private readonly List<UnifiedWorldListItem> _managedGameWorlds = [];
    private readonly List<ImportBrowserCandidate> _importBrowserCandidates = [];

    private WrapPanel? _managedGameTiles;
    private Border? _importWorkspace;
    private WrapPanel? _importGameTiles;
    private TextBox? _importSearchBox;
    private ListBox? _importBrowserList;
    private TextBlock? _importResultText;
    private Button? _importBrowserImportButton;
    private Button? _importBrowserScanButton;

    private string? _selectedManagedGameId;
    private string? _selectedImportGameId;
    private bool _applyingManagedGameFilter;
    private bool _unifiedImportBrowserInitialized;

    internal void InitializeUnifiedImportBrowser()
    {
        if (_unifiedImportBrowserInitialized)
        {
            return;
        }

        _unifiedImportBrowserInitialized = true;

        InitializeManagedGameNavigation();
        InitializeImportWorkspace();
        OpenImportButton.Click += ImportBrowserOpenButton_Click;

        var descriptor = DependencyPropertyDescriptor.FromProperty(
            ItemsControl.ItemsSourceProperty,
            typeof(ListBox));
        descriptor?.AddValueChanged(WorldList, (_, _) => QueueManagedWorldCapture());

        QueueManagedWorldCapture();
    }

    private void InitializeManagedGameNavigation()
    {
        if (WorldList.Parent is not Grid parent)
        {
            throw new InvalidOperationException("The managed World list is not hosted by the expected grid.");
        }

        var row = Grid.GetRow(WorldList);
        parent.Children.Remove(WorldList);

        var host = new Grid();
        host.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        host.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        Grid.SetRow(host, row);
        parent.Children.Add(host);

        var gameScroller = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Margin = new Thickness(0, 0, 0, 12)
        };
        _managedGameTiles = new WrapPanel
        {
            Orientation = Orientation.Horizontal
        };
        gameScroller.Content = _managedGameTiles;
        host.Children.Add(gameScroller);

        Grid.SetRow(WorldList, 1);
        host.Children.Add(WorldList);
    }

    private void QueueManagedWorldCapture()
    {
        if (_applyingManagedGameFilter)
        {
            return;
        }

        Dispatcher.BeginInvoke(
            new Action(CaptureAndFilterManagedWorlds),
            DispatcherPriority.Background);
    }

    private void CaptureAndFilterManagedWorlds()
    {
        if (_applyingManagedGameFilter || WorldList.ItemsSource is not IEnumerable source)
        {
            return;
        }

        var items = source
            .Cast<object>()
            .OfType<UnifiedWorldListItem>()
            .ToList();

        // A filtered view is also an ItemsSource. Do not replace the full library with our own subset.
        var distinctGames = items
            .Select(item => item.World.GameAdapterId)
            .Distinct(StringComparer.Ordinal)
            .Count();
        var knownDistinctGames = _managedGameWorlds
            .Select(item => item.World.GameAdapterId)
            .Distinct(StringComparer.Ordinal)
            .Count();
        if (_managedGameWorlds.Count > 0 &&
            items.Count <= _managedGameWorlds.Count &&
            distinctGames <= 1 &&
            knownDistinctGames > 1)
        {
            return;
        }

        _managedGameWorlds.Clear();
        _managedGameWorlds.AddRange(items);

        if (_managedGameWorlds.Count == 0)
        {
            _managedGameTiles?.Children.Clear();
            return;
        }

        var selectedWorldGame = _selectedWorld?.GameAdapterId;
        if (selectedWorldGame is not null &&
            _managedGameWorlds.Any(item =>
                string.Equals(item.World.GameAdapterId, selectedWorldGame, StringComparison.Ordinal)))
        {
            _selectedManagedGameId = selectedWorldGame;
        }

        _selectedManagedGameId = ResolveAvailableGame(
            _selectedManagedGameId,
            _managedGameWorlds.Select(item => item.World.GameAdapterId));

        ApplyManagedGameFilter(_selectedWorld?.Id);
    }

    private void ApplyManagedGameFilter(WorldId? preferredWorldId = null)
    {
        if (_managedGameWorlds.Count == 0 || string.IsNullOrWhiteSpace(_selectedManagedGameId))
        {
            return;
        }

        var filtered = _managedGameWorlds
            .Where(item => string.Equals(
                item.World.GameAdapterId,
                _selectedManagedGameId,
                StringComparison.Ordinal))
            .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var view = new ListCollectionView(filtered);
        view.SortDescriptions.Add(
            new SortDescription(nameof(UnifiedWorldListItem.Name), ListSortDirection.Ascending));

        _applyingManagedGameFilter = true;
        try
        {
            WorldList.GroupStyle.Clear();
            WorldList.ItemsSource = view;

            var selected = preferredWorldId is { } wanted
                ? filtered.FirstOrDefault(item => item.World.Id == wanted)
                : filtered.FirstOrDefault(item => item.World.Id == _selectedWorld?.Id);
            WorldList.SelectedItem = selected ?? filtered.FirstOrDefault();
            if (WorldList.SelectedItem is not null)
            {
                WorldList.ScrollIntoView(WorldList.SelectedItem);
            }
        }
        finally
        {
            _applyingManagedGameFilter = false;
        }

        RebuildManagedGameTiles();
    }

    private void RebuildManagedGameTiles()
    {
        if (_managedGameTiles is null)
        {
            return;
        }

        _managedGameTiles.Children.Clear();
        var responsibility = _responsibilityTracker.Current;
        foreach (var group in _managedGameWorlds
                     .GroupBy(item => item.World.GameAdapterId, StringComparer.Ordinal)
                     .OrderBy(group => group.First().GameName, StringComparer.OrdinalIgnoreCase))
        {
            var first = group.First();
            var gameId = group.Key;
            var detail = $"{group.Count()} World{(group.Count() == 1 ? string.Empty : "s")}";
            if (responsibility.Kind != WorldLifecycleResponsibilityKind.None &&
                responsibility.WorldId is { } responsibleWorldId &&
                group.Any(item => item.World.Id == responsibleWorldId))
            {
                detail += $" • {FormatResponsibility(responsibility)}";
            }

            var button = CreateGameTileButton(
                first.GameName,
                first.GameIconPath,
                detail,
                string.Equals(gameId, _selectedManagedGameId, StringComparison.Ordinal));
            button.Click += (_, _) =>
            {
                _selectedManagedGameId = gameId;
                ApplyManagedGameFilter();
            };
            _managedGameTiles.Children.Add(button);
        }
    }

    private void InitializeImportWorkspace()
    {
        if (Content is not Grid root)
        {
            throw new InvalidOperationException("The desktop root is not the expected grid.");
        }

        _importWorkspace = new Border
        {
            Visibility = Visibility.Collapsed,
            Margin = new Thickness(24),
            Padding = new Thickness(24),
            Background = (Brush)FindResource("PanelBrush"),
            BorderBrush = (Brush)FindResource("BorderBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(14)
        };
        Grid.SetRow(_importWorkspace, 1);
        Panel.SetZIndex(_importWorkspace, 1000);
        root.Children.Add(_importWorkspace);

        var layout = new Grid();
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _importWorkspace.Child = layout;

        var header = new DockPanel
        {
            LastChildFill = true,
            Margin = new Thickness(0, 0, 0, 18)
        };
        var closeButton = new Button
        {
            Content = "Close",
            MinWidth = 80,
            Margin = new Thickness(12, 0, 0, 0)
        };
        closeButton.Click += (_, _) => CloseImportWorkspace();
        DockPanel.SetDock(closeButton, Dock.Right);
        header.Children.Add(closeButton);

        var heading = new StackPanel();
        heading.Children.Add(new TextBlock
        {
            Text = "Import a local World",
            FontSize = 24,
            FontWeight = FontWeights.SemiBold
        });
        heading.Children.Add(new TextBlock
        {
            Text = "Choose a game first, then select one of its detected Worlds.",
            Margin = new Thickness(0, 5, 0, 0),
            Foreground = (Brush)FindResource("MutedTextBrush")
        });
        header.Children.Add(heading);
        layout.Children.Add(header);

        var gameScroller = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Margin = new Thickness(0, 0, 0, 16)
        };
        _importGameTiles = new WrapPanel { Orientation = Orientation.Horizontal };
        gameScroller.Content = _importGameTiles;
        Grid.SetRow(gameScroller, 1);
        layout.Children.Add(gameScroller);

        var searchGrid = new Grid { Margin = new Thickness(0, 0, 0, 12) };
        searchGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        searchGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _importSearchBox = new TextBox
        {
            MinHeight = 38,
            Padding = new Thickness(10, 7, 10, 7),
            Background = (Brush)FindResource("AppBackgroundBrush"),
            Foreground = (Brush)FindResource("TextBrush"),
            BorderBrush = (Brush)FindResource("BorderBrush"),
            BorderThickness = new Thickness(1),
            ToolTip = "Search by World name or native World id."
        };
        _importSearchBox.TextChanged += (_, _) => ApplyImportBrowserFilter();
        searchGrid.Children.Add(_importSearchBox);

        _importResultText = new TextBlock
        {
            Margin = new Thickness(14, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = (Brush)FindResource("MutedTextBrush")
        };
        Grid.SetColumn(_importResultText, 1);
        searchGrid.Children.Add(_importResultText);
        Grid.SetRow(searchGrid, 2);
        layout.Children.Add(searchGrid);

        _importBrowserList = new ListBox
        {
            Background = (Brush)FindResource("AppBackgroundBrush"),
            Foreground = (Brush)FindResource("TextBrush"),
            BorderBrush = (Brush)FindResource("BorderBrush"),
            BorderThickness = new Thickness(1),
            ItemTemplate = CreateImportCandidateTemplate(),
            ItemContainerStyle = CreateImportBrowserItemStyle()
        };
        ScrollViewer.SetCanContentScroll(_importBrowserList, true);
        ScrollViewer.SetVerticalScrollBarVisibility(_importBrowserList, ScrollBarVisibility.Auto);
        ScrollViewer.SetHorizontalScrollBarVisibility(_importBrowserList, ScrollBarVisibility.Disabled);
        VirtualizingStackPanel.SetIsVirtualizing(_importBrowserList, true);
        VirtualizingStackPanel.SetVirtualizationMode(_importBrowserList, VirtualizationMode.Recycling);
        _importBrowserList.SelectionChanged += (_, _) => UpdateImportBrowserActionState();
        Grid.SetRow(_importBrowserList, 3);
        layout.Children.Add(_importBrowserList);

        var footer = new DockPanel
        {
            LastChildFill = true,
            Margin = new Thickness(0, 16, 0, 0)
        };
        _importBrowserImportButton = new Button
        {
            Content = "Import World",
            MinWidth = 130,
            Style = (Style)FindResource("PrimaryButtonStyle")
        };
        _importBrowserImportButton.Click += ImportBrowserImportButton_Click;
        DockPanel.SetDock(_importBrowserImportButton, Dock.Right);
        footer.Children.Add(_importBrowserImportButton);

        _importBrowserScanButton = new Button
        {
            Content = "Scan again",
            MinWidth = 110,
            Margin = new Thickness(0, 0, 10, 0)
        };
        _importBrowserScanButton.Click += ImportBrowserScanButton_Click;
        DockPanel.SetDock(_importBrowserScanButton, Dock.Right);
        footer.Children.Add(_importBrowserScanButton);

        footer.Children.Add(new TextBlock
        {
            Text = "Detected Worlds stay private until you explicitly share the World.",
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = (Brush)FindResource("MutedTextBrush"),
            TextWrapping = TextWrapping.Wrap
        });
        Grid.SetRow(footer, 4);
        layout.Children.Add(footer);

        UpdateImportBrowserActionState();
    }

    private async void ImportBrowserOpenButton_Click(object sender, RoutedEventArgs e)
    {
        if (_importWorkspace?.Visibility == Visibility.Visible)
        {
            CloseImportWorkspace();
            return;
        }

        _importWorkspace!.Visibility = Visibility.Visible;
        await RefreshImportBrowserAsync();
        _importSearchBox?.Focus();
    }

    private void CloseImportWorkspace()
    {
        if (_importWorkspace is not null)
        {
            _importWorkspace.Visibility = Visibility.Collapsed;
        }
    }

    private async void ImportBrowserScanButton_Click(object sender, RoutedEventArgs e)
        => await RefreshImportBrowserAsync();

    private async void ImportBrowserImportButton_Click(object sender, RoutedEventArgs e)
    {
        if (_importBrowserList?.SelectedItem is not ImportBrowserCandidate candidate)
        {
            return;
        }

        await RunUnifiedOperationAsync(
            $"Importing {candidate.World.DisplayName}...",
            async () =>
            {
                var worldName = await CreateUniqueWorldNameAsync(candidate.World.DisplayName);
                var imported = await _lifecycle.ImportAsync(
                    candidate.Adapter,
                    candidate.Installation,
                    candidate.World,
                    worldName,
                    GetLocalUser());

                await TryEnableCreatorDeviceHostingAsync();
                CloseImportWorkspace();
                StatusText.Text =
                    $"Imported '{imported.Name}' as a private {candidate.GameName} World.";
                await RefreshUnifiedWorldsAsync(imported.Id, preserveStatus: true);
            });
    }

    private async Task RefreshImportBrowserAsync()
    {
        SetBusy(true);
        SetImportBrowserEnabled(false);
        _gamePresentationCache.Clear();
        _importBrowserCandidates.Clear();
        StatusText.Text = "Looking for installed games and local Worlds...";
        _importResultText!.Text = "Scanning...";

        try
        {
            foreach (var adapter in _registeredGameAdapters.Values.OrderBy(value => value.DisplayName))
            {
                var installations = await adapter.DiscoverInstallationsAsync();
                foreach (var installation in installations)
                {
                    var presentation = GetGamePresentation(adapter, installation);
                    var detectedWorlds = await adapter.DiscoverWorldsAsync(installation);
                    foreach (var detectedWorld in detectedWorlds)
                    {
                        _importBrowserCandidates.Add(new ImportBrowserCandidate(
                            adapter,
                            installation,
                            detectedWorld,
                            detectedWorld.DisplayName,
                            installation.Source,
                            presentation.GameName,
                            presentation.IconPath));
                    }
                }
            }

            _importBrowserCandidates.Sort(ImportBrowserCandidateComparer.Instance);
            _selectedImportGameId = ResolveAvailableGame(
                _selectedManagedGameId ?? _selectedImportGameId,
                _importBrowserCandidates.Select(candidate => candidate.Adapter.Id));
            RebuildImportGameTiles();
            ApplyImportBrowserFilter();

            StatusText.Text = _importBrowserCandidates.Count == 1
                ? "1 importable World found."
                : $"{_importBrowserCandidates.Count} importable Worlds found across " +
                  $"{_importBrowserCandidates.Select(candidate => candidate.Adapter.Id).Distinct(StringComparer.Ordinal).Count()} games.";
        }
        catch (Exception exception)
        {
            _importBrowserCandidates.Clear();
            _importBrowserList!.ItemsSource = null;
            _importResultText!.Text = "Discovery failed.";
            StatusText.Text = "World discovery failed.";
            ShowError("Could not discover local Worlds", exception);
        }
        finally
        {
            SetBusy(false);
            SetImportBrowserEnabled(true);
            UpdateImportBrowserActionState();
        }
    }

    private void RebuildImportGameTiles()
    {
        if (_importGameTiles is null)
        {
            return;
        }

        _importGameTiles.Children.Clear();
        foreach (var group in _importBrowserCandidates
                     .GroupBy(candidate => candidate.Adapter.Id, StringComparer.Ordinal)
                     .OrderBy(group => group.First().GameName, StringComparer.OrdinalIgnoreCase))
        {
            var first = group.First();
            var gameId = group.Key;
            var button = CreateGameTileButton(
                first.GameName,
                first.GameIconPath,
                $"{group.Count()} detected",
                string.Equals(gameId, _selectedImportGameId, StringComparison.Ordinal));
            button.Click += (_, _) =>
            {
                _selectedImportGameId = gameId;
                RebuildImportGameTiles();
                ApplyImportBrowserFilter();
            };
            _importGameTiles.Children.Add(button);
        }
    }

    private void ApplyImportBrowserFilter()
    {
        if (_importBrowserList is null)
        {
            return;
        }

        var previousWorldId = (_importBrowserList.SelectedItem as ImportBrowserCandidate)?.World.Id;
        var query = _importSearchBox?.Text.Trim() ?? string.Empty;
        var filtered = _importBrowserCandidates
            .Where(candidate => string.Equals(
                candidate.Adapter.Id,
                _selectedImportGameId,
                StringComparison.Ordinal))
            .Where(candidate => query.Length == 0 ||
                                candidate.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                                candidate.World.Id.Contains(query, StringComparison.OrdinalIgnoreCase))
            .OrderBy(candidate => candidate.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        _importBrowserList.ItemsSource = new ListCollectionView(filtered);
        _importBrowserList.SelectedItem = previousWorldId is null
            ? filtered.FirstOrDefault()
            : filtered.FirstOrDefault(candidate =>
                  string.Equals(candidate.World.Id, previousWorldId, StringComparison.Ordinal))
              ?? filtered.FirstOrDefault();

        var totalForGame = _importBrowserCandidates.Count(candidate =>
            string.Equals(candidate.Adapter.Id, _selectedImportGameId, StringComparison.Ordinal));
        _importResultText!.Text = query.Length == 0
            ? $"{totalForGame} World{(totalForGame == 1 ? string.Empty : "s")}"
            : $"{filtered.Count} of {totalForGame}";

        UpdateImportBrowserActionState();
    }

    private Button CreateGameTileButton(
        string gameName,
        string? iconPath,
        string detail,
        bool selected)
    {
        var button = new Button
        {
            MinWidth = 150,
            MinHeight = 68,
            Margin = new Thickness(0, 0, 10, 8),
            Padding = new Thickness(12, 10, 12, 10),
            Background = selected
                ? (Brush)FindResource("PanelAltBrush")
                : (Brush)FindResource("AppBackgroundBrush"),
            BorderBrush = selected
                ? (Brush)FindResource("AccentBrush")
                : (Brush)FindResource("BorderBrush"),
            BorderThickness = selected ? new Thickness(2) : new Thickness(1)
        };

        var content = new StackPanel { Orientation = Orientation.Horizontal };
        var image = new Image
        {
            Width = 42,
            Height = 42,
            Margin = new Thickness(0, 0, 10, 0),
            Stretch = Stretch.Uniform,
            Source = LoadGameImage(iconPath)
        };
        content.Children.Add(image);

        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock
        {
            Text = gameName,
            FontWeight = FontWeights.SemiBold
        });
        text.Children.Add(new TextBlock
        {
            Text = detail,
            Margin = new Thickness(0, 3, 0, 0),
            FontSize = 11,
            Foreground = (Brush)FindResource("MutedTextBrush")
        });
        content.Children.Add(text);
        button.Content = content;
        return button;
    }

    private Style CreateImportBrowserItemStyle()
    {
        var style = new Style(typeof(ListBoxItem));
        style.Setters.Add(new Setter(Control.ForegroundProperty, FindResource("TextBrush")));
        style.Setters.Add(new Setter(Control.BackgroundProperty, Brushes.Transparent));
        style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(12, 10, 12, 10)));
        style.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch));
        style.Setters.Add(new Setter(Control.BorderBrushProperty, FindResource("BorderBrush")));
        style.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(0, 0, 0, 1)));

        var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
        hover.Setters.Add(new Setter(Control.BackgroundProperty, FindResource("PanelAltBrush")));
        style.Triggers.Add(hover);

        var selected = new Trigger { Property = ListBoxItem.IsSelectedProperty, Value = true };
        selected.Setters.Add(new Setter(Control.BackgroundProperty, FindResource("PanelAltBrush")));
        selected.Setters.Add(new Setter(Control.BorderBrushProperty, FindResource("AccentBrush")));
        selected.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(2)));
        style.Triggers.Add(selected);

        return style;
    }

    private void UpdateImportBrowserActionState()
    {
        if (_importBrowserImportButton is not null)
        {
            _importBrowserImportButton.IsEnabled =
                !_isBusy && _importBrowserList?.SelectedItem is ImportBrowserCandidate;
        }
    }

    private void SetImportBrowserEnabled(bool enabled)
    {
        if (_importSearchBox is not null)
        {
            _importSearchBox.IsEnabled = enabled;
        }

        if (_importBrowserList is not null)
        {
            _importBrowserList.IsEnabled = enabled;
        }

        if (_importBrowserScanButton is not null)
        {
            _importBrowserScanButton.IsEnabled = enabled;
        }
    }

    private static string? ResolveAvailableGame(string? preferred, IEnumerable<string> gameIds)
    {
        var available = gameIds
            .Distinct(StringComparer.Ordinal)
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();
        return preferred is not null && available.Contains(preferred, StringComparer.Ordinal)
            ? preferred
            : available.FirstOrDefault();
    }

    private static ImageSource? LoadGameImage(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return null;
        }

        try
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.UriSource = new Uri(Path.GetFullPath(path), UriKind.Absolute);
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return null;
        }
    }

    private sealed record ImportBrowserCandidate(
        IGameAdapter Adapter,
        GameInstallation Installation,
        DetectedWorld World,
        string Name,
        string Subtitle,
        string GameName,
        string? GameIconPath);

    private sealed class ImportBrowserCandidateComparer : IComparer<ImportBrowserCandidate>
    {
        public static ImportBrowserCandidateComparer Instance { get; } = new();

        public int Compare(ImportBrowserCandidate? left, ImportBrowserCandidate? right)
        {
            if (ReferenceEquals(left, right))
            {
                return 0;
            }

            if (left is null)
            {
                return -1;
            }

            if (right is null)
            {
                return 1;
            }

            var gameComparison = StringComparer.OrdinalIgnoreCase.Compare(left.GameName, right.GameName);
            return gameComparison != 0
                ? gameComparison
                : StringComparer.OrdinalIgnoreCase.Compare(left.Name, right.Name);
        }
    }
}
