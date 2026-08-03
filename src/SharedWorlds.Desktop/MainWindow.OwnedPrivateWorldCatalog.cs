using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using SharedWorlds.Core.Domain;
using SharedWorlds.Core.Worlds;
using SharedWorlds.Infrastructure.Remote;

namespace SharedWorlds.Desktop;

public partial class MainWindow
{
    private readonly List<OwnedPrivateWorldListItem> _ownedPrivateWorldItems = [];

    private Border? _ownedPrivateWorldSection;
    private ListBox? _ownedPrivateWorldList;
    private TextBlock? _ownedPrivateWorldCatalogStatusText;
    private StackPanel? _ownedPrivateWorldDetailsPanel;
    private TextBlock? _ownedPrivateWorldNameText;
    private TextBlock? _ownedPrivateWorldGameText;
    private TextBlock? _ownedPrivateWorldAvailabilityText;
    private TextBlock? _ownedPrivateWorldReasonText;
    private TextBlock? _ownedPrivateWorldLocationText;
    private TextBlock? _ownedPrivateWorldWorldIdText;
    private TextBlock? _ownedPrivateWorldStateText;
    private TextBlock? _ownedPrivateWorldEnvironmentText;
    private OwnedPrivateWorldListItem? _selectedOwnedPrivateWorld;
    private Exception? _lastOwnedPrivateWorldCatalogError;

    private void InitializeOwnedPrivateWorldCatalogUi()
    {
        if (GameLibraryList.Parent is not StackPanel gamesHome ||
            WorldDetailsPanel.Parent is not StackPanel detailsRoot)
        {
            throw new InvalidOperationException(
                "The Safe World shell no longer exposes the expected catalog presentation surfaces.");
        }

        _ownedPrivateWorldCatalogStatusText = new TextBlock
        {
            Margin = new Thickness(0, 6, 0, 0),
            FontSize = 12,
            Foreground = (Brush)FindResource("MutedTextBrush"),
            TextWrapping = TextWrapping.Wrap
        };

        _ownedPrivateWorldList = new ListBox
        {
            Margin = new Thickness(0, 14, 0, 0),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            MaxHeight = 280,
            ItemContainerStyle = WorldList.ItemContainerStyle,
            ItemTemplate = CreateOwnedPrivateWorldItemTemplate(),
            ItemsSource = _ownedPrivateWorldItems
        };
        AutomationProperties.SetName(
            _ownedPrivateWorldList,
            "Private Worlds on another owned PC");
        _ownedPrivateWorldList.SelectionChanged += OwnedPrivateWorldList_SelectionChanged;

        var sectionContent = new StackPanel();
        sectionContent.Children.Add(new TextBlock
        {
            Text = "On another PC",
            FontSize = 19,
            FontWeight = FontWeights.SemiBold
        });
        sectionContent.Children.Add(new TextBlock
        {
            Text = "Private Worlds discovered from your other Safe World installations.",
            Margin = new Thickness(0, 5, 0, 0),
            FontSize = 12,
            Foreground = (Brush)FindResource("MutedTextBrush"),
            TextWrapping = TextWrapping.Wrap
        });
        sectionContent.Children.Add(_ownedPrivateWorldCatalogStatusText);
        sectionContent.Children.Add(_ownedPrivateWorldList);

        _ownedPrivateWorldSection = new Border
        {
            Margin = new Thickness(0, 28, 0, 0),
            Padding = new Thickness(22),
            Style = (Style)FindResource("CardBorderStyle"),
            Visibility = Visibility.Collapsed,
            Child = sectionContent
        };
        var gameLibraryIndex = gamesHome.Children.IndexOf(GameLibraryList);
        gamesHome.Children.Insert(gameLibraryIndex + 1, _ownedPrivateWorldSection);

        _ownedPrivateWorldDetailsPanel = CreateOwnedPrivateWorldDetailsPanel();
        _ownedPrivateWorldDetailsPanel.Visibility = Visibility.Collapsed;
        detailsRoot.Children.Add(_ownedPrivateWorldDetailsPanel);
    }

    private void InitializeOwnedPrivateWorldCatalogRefreshHooks()
    {
        RefreshButton.Click += async (_, _) =>
            await RefreshOwnedPrivateWorldCatalogAsync();
        SizeChanged += (_, _) => ApplyOwnedPrivateWorldDetailsLayout();
    }

    private async Task RefreshOwnedPrivateWorldCatalogAsync(
        CancellationToken cancellationToken = default)
    {
        var list = _ownedPrivateWorldList;
        var section = _ownedPrivateWorldSection;
        var status = _ownedPrivateWorldCatalogStatusText;
        if (list is null || section is null || status is null)
        {
            return;
        }

        var remote = Volatile.Read(ref _remoteRuntime);
        if (remote is null)
        {
            _ownedPrivateWorldItems.Clear();
            list.Items.Refresh();
            section.Visibility = Visibility.Collapsed;
            _lastOwnedPrivateWorldCatalogError = null;
            return;
        }

        try
        {
            var catalog = await remote.ListOwnedPrivateWorldsAsync(cancellationToken);
            var materializedWorldIds = _allWorldItems
                .Select(item => item.World.Id)
                .ToHashSet();
            var items = new List<OwnedPrivateWorldListItem>();
            foreach (var entry in catalog)
            {
                if (materializedWorldIds.Contains(entry.WorldId) ||
                    entry.Availability == BringHereAvailability.AlreadyHere)
                {
                    continue;
                }

                var gameName = entry.GameAdapterId is null
                    ? "Game identity conflict"
                    : (await GetGamePresentationAsync(entry.GameAdapterId)).GameName;
                var iconPath = entry.GameAdapterId is null
                    ? null
                    : (await GetGamePresentationAsync(entry.GameAdapterId)).IconPath;
                items.Add(new OwnedPrivateWorldListItem(
                    entry,
                    entry.Name,
                    FormatOwnedPrivateWorldSubtitle(entry, gameName),
                    gameName,
                    iconPath));
            }

            _ownedPrivateWorldItems.Clear();
            _ownedPrivateWorldItems.AddRange(items
                .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(item => item.Entry.WorldId.ToString(), StringComparer.Ordinal));
            list.Items.Refresh();
            _lastOwnedPrivateWorldCatalogError = null;
            status.Text = _ownedPrivateWorldItems.Count == 0
                ? "No private Worlds are currently available only on another PC."
                : $"{_ownedPrivateWorldItems.Count} private World{(_ownedPrivateWorldItems.Count == 1 ? string.Empty : "s")} discovered.";
            section.Visibility = _ownedPrivateWorldItems.Count == 0
                ? Visibility.Collapsed
                : Visibility.Visible;

            if (_selectedOwnedPrivateWorld is not null)
            {
                var refreshed = _ownedPrivateWorldItems.FirstOrDefault(item =>
                    item.Entry.WorldId == _selectedOwnedPrivateWorld.Entry.WorldId);
                if (refreshed is null)
                {
                    ShowGamesLibraryFromOwnedPrivateWorld();
                }
                else
                {
                    ShowOwnedPrivateWorldDetails(refreshed);
                }
            }
        }
        catch (Exception exception) when (IsRemoteAvailabilityFailure(exception))
        {
            _ownedPrivateWorldItems.Clear();
            list.Items.Refresh();
            _lastOwnedPrivateWorldCatalogError = exception;
            status.Text = "Private Worlds on other PCs could not be loaded. Local Worlds remain available.";
            section.Visibility = Visibility.Visible;
        }
    }

    private void OwnedPrivateWorldList_SelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        if (_ownedPrivateWorldList?.SelectedItem is not OwnedPrivateWorldListItem item)
        {
            return;
        }

        ShowOwnedPrivateWorldDetails(item);
    }

    private void ShowOwnedPrivateWorldDetails(OwnedPrivateWorldListItem item)
    {
        _selectedOwnedPrivateWorld = item;
        _selectedWorld = null;
        WorldList.SelectedItem = null;
        GameLibraryList.SelectedItem = null;
        WorldDetailsPanel.Visibility = Visibility.Collapsed;
        EmptyStateText.Visibility = Visibility.Collapsed;
        BackToWorldsButton.Visibility = Visibility.Collapsed;
        PopulateOwnedPrivateWorldDetails(item);
        ApplyOwnedPrivateWorldDetailsLayout();
    }

    private void ShowGamesLibraryFromOwnedPrivateWorld()
    {
        _selectedOwnedPrivateWorld = null;
        if (_ownedPrivateWorldList is not null)
        {
            _ownedPrivateWorldList.SelectedItem = null;
        }

        if (_ownedPrivateWorldDetailsPanel is not null)
        {
            _ownedPrivateWorldDetailsPanel.Visibility = Visibility.Collapsed;
        }

        WorldDetailsScroll.Visibility = Visibility.Collapsed;
        WorldSidebar.Visibility = Visibility.Collapsed;
        GamesLibraryPanel.Visibility = Visibility.Visible;
        BackToGamesButton.Visibility = Visibility.Collapsed;
        EmptyStateText.Visibility = Visibility.Visible;
    }

    private void ApplyOwnedPrivateWorldDetailsLayout()
    {
        if (_selectedOwnedPrivateWorld is null ||
            _ownedPrivateWorldDetailsPanel is null)
        {
            return;
        }

        GamesLibraryPanel.Visibility = Visibility.Collapsed;
        WorldSidebar.Visibility = Visibility.Collapsed;
        WorldDetailsScroll.Visibility = Visibility.Visible;
        WorldDetailsPanel.Visibility = Visibility.Collapsed;
        EmptyStateText.Visibility = Visibility.Collapsed;
        BackToWorldsButton.Visibility = Visibility.Collapsed;
        _ownedPrivateWorldDetailsPanel.Visibility = Visibility.Visible;
    }

    private void PopulateOwnedPrivateWorldDetails(OwnedPrivateWorldListItem item)
    {
        var entry = item.Entry;
        _ownedPrivateWorldNameText!.Text = entry.Name;
        _ownedPrivateWorldGameText!.Text = item.GameName;
        _ownedPrivateWorldAvailabilityText!.Text = entry.Availability switch
        {
            BringHereAvailability.Available => "Available on another owned PC",
            BringHereAvailability.Conflict => "Needs attention before it can be brought here",
            _ => entry.Availability.ToString()
        };
        _ownedPrivateWorldReasonText!.Text = entry.Reason;
        _ownedPrivateWorldWorldIdText!.Text = entry.WorldId.ToString();

        if (entry.Source is { } source)
        {
            _ownedPrivateWorldLocationText!.Text =
                $"Source installation: {source.InstallationId}";
            _ownedPrivateWorldStateText!.Text = source.StateRevisionId.ToString();
            _ownedPrivateWorldEnvironmentText!.Text = source.EnvironmentRevisionId.ToString();
        }
        else
        {
            _ownedPrivateWorldLocationText!.Text =
                $"Conflicting owned installations: {entry.ConflictingClaims.Count}";
            _ownedPrivateWorldStateText!.Text = "No source selected";
            _ownedPrivateWorldEnvironmentText!.Text = "No source selected";
        }
    }

    private StackPanel CreateOwnedPrivateWorldDetailsPanel()
    {
        var panel = new StackPanel();
        var back = new Button
        {
            Content = "Back to games",
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 0, 0, 22),
            Style = (Style)FindResource("GhostButtonStyle")
        };
        back.Click += (_, _) => ShowGamesLibraryFromOwnedPrivateWorld();
        panel.Children.Add(back);
        panel.Children.Add(new Border
        {
            Width = 48,
            Height = 4,
            Margin = new Thickness(0, 0, 0, 16),
            HorizontalAlignment = HorizontalAlignment.Left,
            Background = (Brush)FindResource("BrandGradientBrush"),
            CornerRadius = new CornerRadius(2)
        });

        _ownedPrivateWorldNameText = new TextBlock
        {
            FontSize = 38,
            FontWeight = FontWeights.Bold,
            TextWrapping = TextWrapping.Wrap
        };
        panel.Children.Add(_ownedPrivateWorldNameText);
        _ownedPrivateWorldGameText = CreateMutedText(15, new Thickness(0, 7, 0, 0));
        panel.Children.Add(_ownedPrivateWorldGameText);

        _ownedPrivateWorldAvailabilityText = new TextBlock
        {
            Margin = new Thickness(0, 18, 0, 0),
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap
        };
        panel.Children.Add(_ownedPrivateWorldAvailabilityText);

        var explanation = new StackPanel();
        explanation.Children.Add(new TextBlock
        {
            Text = "Remote private World",
            FontSize = 17,
            FontWeight = FontWeights.SemiBold
        });
        explanation.Children.Add(new TextBlock
        {
            Text = "This World is not stored on this PC. Safe World will not start, host, share, or delete it from this read-only view.",
            Margin = new Thickness(0, 6, 0, 0),
            FontSize = 12,
            Foreground = (Brush)FindResource("MutedTextBrush"),
            TextWrapping = TextWrapping.Wrap
        });
        _ownedPrivateWorldReasonText = CreateMutedText(13, new Thickness(0, 14, 0, 0));
        explanation.Children.Add(_ownedPrivateWorldReasonText);
        _ownedPrivateWorldLocationText = CreateMutedText(12, new Thickness(0, 12, 0, 0));
        explanation.Children.Add(_ownedPrivateWorldLocationText);
        panel.Children.Add(new Border
        {
            Margin = new Thickness(0, 22, 0, 0),
            Padding = new Thickness(22),
            Style = (Style)FindResource("CardBorderStyle"),
            Child = explanation
        });

        var technical = new Grid();
        technical.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        technical.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (var row = 0; row < 3; row++)
        {
            technical.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        }

        _ownedPrivateWorldWorldIdText = AddTechnicalRow(technical, 0, "World ID");
        _ownedPrivateWorldStateText = AddTechnicalRow(technical, 1, "State");
        _ownedPrivateWorldEnvironmentText = AddTechnicalRow(technical, 2, "Environment");
        panel.Children.Add(new Border
        {
            Margin = new Thickness(0, 18, 0, 0),
            Padding = new Thickness(20),
            Style = (Style)FindResource("CardBorderStyle"),
            Child = technical
        });

        return panel;
    }

    private TextBlock AddTechnicalRow(Grid grid, int row, string label)
    {
        var labelText = CreateMutedText(12, new Thickness(0, 0, 24, row == 2 ? 0 : 10));
        labelText.Text = label;
        Grid.SetRow(labelText, row);
        Grid.SetColumn(labelText, 0);
        grid.Children.Add(labelText);

        var value = new TextBlock
        {
            Margin = new Thickness(0, 0, 0, row == 2 ? 0 : 10),
            TextWrapping = TextWrapping.Wrap
        };
        Grid.SetRow(value, row);
        Grid.SetColumn(value, 1);
        grid.Children.Add(value);
        return value;
    }

    private TextBlock CreateMutedText(double fontSize, Thickness margin)
        => new()
        {
            Margin = margin,
            FontSize = fontSize,
            Foreground = (Brush)FindResource("MutedTextBrush"),
            TextWrapping = TextWrapping.Wrap
        };

    private static string FormatOwnedPrivateWorldSubtitle(
        StewardOwnedPrivateWorldCatalogEntry entry,
        string gameName)
        => entry.Availability switch
        {
            BringHereAvailability.Available =>
                $"{gameName} • available on another PC",
            BringHereAvailability.Conflict =>
                $"{gameName} • needs attention",
            _ => $"{gameName} • {entry.Availability}"
        };

    private static DataTemplate CreateOwnedPrivateWorldItemTemplate()
    {
        var root = new FrameworkElementFactory(typeof(DockPanel));
        root.SetValue(DockPanel.LastChildFillProperty, true);

        var icon = new FrameworkElementFactory(typeof(Image));
        icon.SetValue(FrameworkElement.WidthProperty, 40d);
        icon.SetValue(FrameworkElement.HeightProperty, 40d);
        icon.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 0, 8, 0));
        icon.SetValue(Image.StretchProperty, Stretch.Uniform);
        icon.SetValue(DockPanel.DockProperty, Dock.Left);
        icon.SetBinding(
            Image.SourceProperty,
            new Binding(nameof(OwnedPrivateWorldListItem.GameIconPath)));
        root.AppendChild(icon);

        var text = new FrameworkElementFactory(typeof(StackPanel));
        var name = new FrameworkElementFactory(typeof(TextBlock));
        name.SetValue(TextBlock.FontSizeProperty, 15d);
        name.SetValue(TextBlock.FontWeightProperty, FontWeights.SemiBold);
        name.SetBinding(
            TextBlock.TextProperty,
            new Binding(nameof(OwnedPrivateWorldListItem.Name)));
        text.AppendChild(name);

        var subtitle = new FrameworkElementFactory(typeof(TextBlock));
        subtitle.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 5, 0, 0));
        subtitle.SetValue(TextBlock.FontSizeProperty, 12d);
        subtitle.SetValue(TextBlock.TextWrappingProperty, TextWrapping.Wrap);
        subtitle.SetBinding(
            TextBlock.TextProperty,
            new Binding(nameof(OwnedPrivateWorldListItem.Subtitle)));
        text.AppendChild(subtitle);
        root.AppendChild(text);

        return new DataTemplate { VisualTree = root };
    }

    private void SetOwnedPrivateWorldCatalogBusyState(bool isBusy)
    {
        if (_ownedPrivateWorldList is not null)
        {
            _ownedPrivateWorldList.IsEnabled = !isBusy;
        }
    }

    private sealed record OwnedPrivateWorldListItem(
        StewardOwnedPrivateWorldCatalogEntry Entry,
        string Name,
        string Subtitle,
        string GameName,
        string? GameIconPath);
}
