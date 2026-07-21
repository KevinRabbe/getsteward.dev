using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using SharedWorlds.Core.Abstractions;

namespace SharedWorlds.Desktop;

public partial class MainWindow
{
    private readonly List<ImportBrowserCandidate> _importBrowserCandidates = [];
    private TextBox? _importSearchBox;
    private ListBox? _importBrowserList;
    private bool _unifiedImportBrowserInitialized;

    internal void InitializeUnifiedImportBrowser()
    {
        if (_unifiedImportBrowserInitialized)
        {
            return;
        }

        _unifiedImportBrowserInitialized = true;

        OpenImportButton.Click -= UnifiedOpenImportButton_Click;
        OpenImportButton.Click += ImportBrowserOpenButton_Click;
        ScanImportsButton.Click -= UnifiedScanImportsButton_Click;
        ScanImportsButton.Click += ImportBrowserScanButton_Click;
        ImportSelectedButton.Click -= UnifiedImportSelectedButton_Click;
        ImportSelectedButton.Click += ImportBrowserImportButton_Click;

        ImportCandidateComboBox.Visibility = Visibility.Collapsed;

        if (ImportCandidateComboBox.Parent is not Panel parent)
        {
            throw new InvalidOperationException("The import selector is not hosted by a panel.");
        }

        var browser = new StackPanel();

        var searchLabel = new TextBlock
        {
            Text = "Search Worlds",
            Margin = new Thickness(0, 0, 0, 5),
            FontSize = 12,
            FontWeight = FontWeights.SemiBold
        };
        browser.Children.Add(searchLabel);

        _importSearchBox = new TextBox
        {
            MinHeight = 34,
            Margin = new Thickness(0, 0, 0, 10),
            Padding = new Thickness(9, 6, 9, 6),
            Background = (Brush)FindResource("PanelBrush"),
            Foreground = (Brush)FindResource("TextBrush"),
            BorderBrush = (Brush)FindResource("BorderBrush"),
            BorderThickness = new Thickness(1),
            ToolTip = "Search by game, World name or native World id."
        };
        _importSearchBox.TextChanged += (_, _) => ApplyImportBrowserFilter();
        browser.Children.Add(_importSearchBox);

        _importBrowserList = new ListBox
        {
            Height = 320,
            Background = (Brush)FindResource("PanelBrush"),
            Foreground = (Brush)FindResource("TextBrush"),
            BorderBrush = (Brush)FindResource("BorderBrush"),
            BorderThickness = new Thickness(1),
            ItemTemplate = CreateImportCandidateTemplate(),
            ItemContainerStyle = CreateImportBrowserItemStyle(),
            ScrollViewer = { }
        };
        ScrollViewer.SetCanContentScroll(_importBrowserList, true);
        ScrollViewer.SetVerticalScrollBarVisibility(_importBrowserList, ScrollBarVisibility.Auto);
        ScrollViewer.SetHorizontalScrollBarVisibility(_importBrowserList, ScrollBarVisibility.Disabled);
        VirtualizingStackPanel.SetIsVirtualizing(_importBrowserList, true);
        VirtualizingStackPanel.SetVirtualizationMode(
            _importBrowserList,
            VirtualizationMode.Recycling);
        _importBrowserList.GroupStyle.Add(CreateGameGroupStyle());
        _importBrowserList.SelectionChanged += (_, _) => UpdateImportBrowserActionState();
        browser.Children.Add(_importBrowserList);

        var index = parent.Children.IndexOf(ImportCandidateComboBox);
        parent.Children.Insert(index < 0 ? 0 : index + 1, browser);

        UpdateImportBrowserActionState();
    }

    private async void ImportBrowserOpenButton_Click(object sender, RoutedEventArgs e)
    {
        if (ImportPanel.Visibility == Visibility.Visible)
        {
            ImportPanel.Visibility = Visibility.Collapsed;
            return;
        }

        ImportPanel.Visibility = Visibility.Visible;
        await RefreshImportBrowserAsync();
        _importSearchBox?.Focus();
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
                ImportPanel.Visibility = Visibility.Collapsed;
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
        ImportDiscoveryText.Text = "Scanning registered game adapters...";

        try
        {
            var installedGameCount = 0;

            foreach (var adapter in _registeredGameAdapters.Values.OrderBy(value => value.DisplayName))
            {
                var installations = await adapter.DiscoverInstallationsAsync();
                if (installations.Count > 0)
                {
                    installedGameCount++;
                }

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
                            $"{presentation.GameName}  •  {installation.Source}",
                            presentation.GameName,
                            presentation.IconPath));
                    }
                }
            }

            _importBrowserCandidates.Sort(ImportBrowserCandidateComparer.Instance);
            ApplyImportBrowserFilter();

            if (installedGameCount == 0)
            {
                ImportDiscoveryText.Text = "No supported installed games were detected on this device.";
                StatusText.Text = "No supported game installation found.";
            }
            else if (_importBrowserCandidates.Count == 0)
            {
                ImportDiscoveryText.Text =
                    $"{installedGameCount} supported game installation(s) found, but no importable Worlds were detected.";
                StatusText.Text = "No importable Worlds found.";
            }
            else
            {
                StatusText.Text = _importBrowserCandidates.Count == 1
                    ? "1 importable World found."
                    : $"{_importBrowserCandidates.Count} importable Worlds found across {installedGameCount} installed games.";
            }
        }
        catch (Exception exception)
        {
            _importBrowserCandidates.Clear();
            _importBrowserList!.ItemsSource = null;
            ImportDiscoveryText.Text = "Could not scan installed games and Worlds.";
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

    private void ApplyImportBrowserFilter()
    {
        if (_importBrowserList is null)
        {
            return;
        }

        var previousWorldId = (_importBrowserList.SelectedItem as ImportBrowserCandidate)?.World.Id;
        var query = _importSearchBox?.Text.Trim() ?? string.Empty;
        var filtered = _importBrowserCandidates
            .Where(candidate => MatchesImportSearch(candidate, query))
            .ToList();

        var view = new ListCollectionView(filtered);
        view.GroupDescriptions.Add(
            new PropertyGroupDescription(nameof(ImportBrowserCandidate.GameName)));
        view.SortDescriptions.Add(
            new SortDescription(nameof(ImportBrowserCandidate.GameName), ListSortDirection.Ascending));
        view.SortDescriptions.Add(
            new SortDescription(nameof(ImportBrowserCandidate.Name), ListSortDirection.Ascending));

        _importBrowserList.ItemsSource = view;
        _importBrowserList.SelectedItem = previousWorldId is null
            ? filtered.FirstOrDefault()
            : filtered.FirstOrDefault(candidate =>
                  string.Equals(candidate.World.Id, previousWorldId, StringComparison.Ordinal))
              ?? filtered.FirstOrDefault();

        ImportDiscoveryText.Text = query.Length == 0
            ? _importBrowserCandidates.Count == 1
                ? "1 importable World found."
                : $"{_importBrowserCandidates.Count} importable Worlds grouped by game."
            : filtered.Count == 1
                ? $"1 of {_importBrowserCandidates.Count} Worlds matches."
                : $"{filtered.Count} of {_importBrowserCandidates.Count} Worlds match.";

        UpdateImportBrowserActionState();
    }

    private static bool MatchesImportSearch(ImportBrowserCandidate candidate, string query)
    {
        if (query.Length == 0)
        {
            return true;
        }

        return candidate.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
               candidate.GameName.Contains(query, StringComparison.OrdinalIgnoreCase) ||
               candidate.World.Id.Contains(query, StringComparison.OrdinalIgnoreCase);
    }

    private void UpdateImportBrowserActionState()
    {
        ImportSelectedButton.IsEnabled =
            !_isBusy && _importBrowserList?.SelectedItem is ImportBrowserCandidate;
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
    }

    private Style CreateImportBrowserItemStyle()
    {
        var style = new Style(typeof(ListBoxItem));
        style.Setters.Add(new Setter(Control.ForegroundProperty, FindResource("TextBrush")));
        style.Setters.Add(new Setter(Control.BackgroundProperty, Brushes.Transparent));
        style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(10, 8, 10, 8)));
        style.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch));
        style.Setters.Add(new Setter(Control.BorderBrushProperty, FindResource("BorderBrush")));
        style.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(0, 0, 0, 1)));

        var hover = new Trigger
        {
            Property = UIElement.IsMouseOverProperty,
            Value = true
        };
        hover.Setters.Add(new Setter(Control.BackgroundProperty, FindResource("PanelAltBrush")));
        style.Triggers.Add(hover);

        var selected = new Trigger
        {
            Property = ListBoxItem.IsSelectedProperty,
            Value = true
        };
        selected.Setters.Add(new Setter(Control.BackgroundProperty, FindResource("AccentBrush")));
        selected.Setters.Add(new Setter(Control.ForegroundProperty, Brushes.White));
        style.Triggers.Add(selected);

        return style;
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
