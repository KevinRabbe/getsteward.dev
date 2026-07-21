using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using SharedWorlds.Core.Abstractions;
using SharedWorlds.Core.Domain;
using SharedWorlds.GameAdapters.Factorio;
using SharedWorlds.GameAdapters.Palworld;

namespace SharedWorlds.Desktop;

public partial class MainWindow
{
    private readonly IReadOnlyDictionary<string, IGameAdapter> _registeredGameAdapters =
        new Dictionary<string, IGameAdapter>(StringComparer.Ordinal)
        {
            ["factorio"] = new FactorioAdapter(),
            ["palworld"] = new PalworldAdapter()
        };

    private readonly GameIconResolver _gameIconResolver = new();
    private readonly Dictionary<string, GamePresentation> _gamePresentationCache =
        new(StringComparer.Ordinal);
    private bool _unifiedGameUiInitialized;

    internal async void InitializeUnifiedGameUi()
    {
        if (_unifiedGameUiInitialized)
        {
            return;
        }

        _unifiedGameUiInitialized = true;

        RefreshButton.Click -= RefreshButton_Click;
        RefreshButton.Click += UnifiedRefreshButton_Click;
        OpenImportButton.Click -= OpenImportButton_Click;
        OpenImportButton.Click += UnifiedOpenImportButton_Click;
        ScanImportsButton.Click -= ScanImportsButton_Click;
        ScanImportsButton.Click += UnifiedScanImportsButton_Click;
        ImportSelectedButton.Click -= ImportSelectedButton_Click;
        ImportSelectedButton.Click += UnifiedImportSelectedButton_Click;
        ImportCandidateComboBox.SelectionChanged -= ImportCandidateComboBox_SelectionChanged;
        ImportCandidateComboBox.SelectionChanged += UnifiedImportCandidateComboBox_SelectionChanged;
        ContinueButton.Click -= ContinueButton_Click;
        ContinueButton.Click += UnifiedContinueButton_Click;
        HostButton.Click -= HostButton_Click;
        HostButton.Click += UnifiedHostButton_Click;
        ShareButton.Click -= ShareButton_Click;
        ShareButton.Click += UnifiedShareButton_Click;

        WorldList.SelectionChanged += UnifiedWorldList_SelectionChanged;
        WorldList.IsEnabledChanged += (_, _) => UpdateUnifiedActionState();
        ImportCandidateComboBox.IsEnabledChanged += (_, _) => UpdateUnifiedImportActionState();
        AllowHostingCheckBox.Click += (_, _) => UpdateUnifiedActionState();

        WorldList.ItemTemplate = CreateWorldItemTemplate();
        ImportCandidateComboBox.ItemTemplate = CreateImportCandidateTemplate();
        WorldList.GroupStyle.Clear();
        WorldList.GroupStyle.Add(CreateGameGroupStyle());

        ContinueButton.Visibility = Visibility.Visible;
        HostButton.Visibility = Visibility.Visible;

        await RefreshUnifiedWorldsAsync();
    }

    private async void UnifiedRefreshButton_Click(object sender, RoutedEventArgs e)
        => await RefreshUnifiedWorldsAsync(_selectedWorld?.Id);

    private async void UnifiedOpenImportButton_Click(object sender, RoutedEventArgs e)
    {
        if (ImportPanel.Visibility == Visibility.Visible)
        {
            ImportPanel.Visibility = Visibility.Collapsed;
            return;
        }

        ImportPanel.Visibility = Visibility.Visible;
        await RefreshUnifiedImportCandidatesAsync();
    }

    private async void UnifiedScanImportsButton_Click(object sender, RoutedEventArgs e)
        => await RefreshUnifiedImportCandidatesAsync();

    private void UnifiedImportCandidateComboBox_SelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
        => UpdateUnifiedImportActionState();

    private async void UnifiedImportSelectedButton_Click(object sender, RoutedEventArgs e)
    {
        if (ImportCandidateComboBox.SelectedItem is not UnifiedImportCandidate candidate)
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
                StatusText.Text = $"Imported '{imported.Name}' as a private {candidate.GameName} World.";
                await RefreshUnifiedWorldsAsync(imported.Id, preserveStatus: true);
            });
    }

    private void UnifiedWorldList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (WorldList.SelectedItem is not UnifiedWorldListItem selected)
        {
            _selectedWorld = null;
            EmptyStateText.Visibility = Visibility.Visible;
            WorldDetailsPanel.Visibility = Visibility.Collapsed;
            UpdateUnifiedActionState();
            return;
        }

        _selectedWorld = selected.World;
        EmptyStateText.Visibility = Visibility.Collapsed;
        WorldDetailsPanel.Visibility = Visibility.Visible;
        WorldNameText.Text = selected.World.Name;
        GameText.Text = selected.GameName;
        SharingText.Text = FormatSharingMode(selected.World.SharingMode);
        VersionText.Text = $"Version {selected.GameVersion}";
        WorldIdText.Text = selected.World.Id.ToString();
        EnvironmentRevisionText.Text = selected.World.CurrentEnvironmentRevisionId?.ToString() ?? "none";
        StateRevisionText.Text = selected.World.CurrentStateRevisionId?.ToString() ?? "none";
        UpdateUnifiedActionState();
    }

    private async void UnifiedContinueButton_Click(object sender, RoutedEventArgs e)
    {
        var world = _selectedWorld;
        if (world is null || !TryGetAdapter(world.GameAdapterId, out var adapter))
        {
            return;
        }

        if (!adapter.Capabilities.HasFlag(GameAdapterCapabilities.AutomaticLocalLaunch))
        {
            StatusText.Text = $"{adapter.DisplayName} does not support Steward-managed local launch yet.";
            return;
        }

        await RunUnifiedOperationAsync(
            $"Starting {world.Name}...",
            async () =>
            {
                var installation = await GetGameInstallationAsync(adapter);
                var updated = await _lifecycle.ContinueLocalAsync(
                    world.Id,
                    adapter,
                    installation,
                    GetLocalUser());

                StatusText.Text =
                    $"World '{updated.Name}' committed as revision {updated.CurrentStateRevisionId}.";
                await RefreshUnifiedWorldsAsync(updated.Id, preserveStatus: true);
            });
    }

    private async void UnifiedHostButton_Click(object sender, RoutedEventArgs e)
    {
        var world = _selectedWorld;
        if (world is null || !TryGetAdapter(world.GameAdapterId, out var adapter))
        {
            return;
        }

        if (!_deviceSettings.AllowHosting)
        {
            StatusText.Text = "Enable 'Allow this device to host' in Device settings before hosting.";
            return;
        }

        if (!adapter.Capabilities.HasFlag(GameAdapterCapabilities.AutomaticHostLaunch))
        {
            StatusText.Text = $"{adapter.DisplayName} does not support Steward-managed hosting yet.";
            return;
        }

        await RunUnifiedOperationAsync(
            $"Hosting {world.Name}...",
            async () =>
            {
                var installation = await GetGameInstallationAsync(adapter);
                StatusText.Text =
                    $"{adapter.DisplayName} is running. End the game/server session normally; Steward will then capture and commit the new canonical revision.";

                var updated = await _lifecycle.ContinueAsHostAsync(
                    world.Id,
                    adapter,
                    installation,
                    GetLocalUser());

                StatusText.Text =
                    $"Hosted World '{updated.Name}' committed as revision {updated.CurrentStateRevisionId}.";
                await RefreshUnifiedWorldsAsync(updated.Id, preserveStatus: true);
            });
    }

    private void UnifiedShareButton_Click(object sender, RoutedEventArgs e)
    {
        var world = _selectedWorld;
        if (world is null)
        {
            return;
        }

        // A local enum flip is not persistent Steward sharing. Until BE-2/UI-4 can create or
        // administer real shared authority, keep the World unchanged and expose the honest entry
        // point rather than manufacturing a false Shared state.
        StatusText.Text = world.SharingMode == WorldSharingMode.LocalOnly
            ? "Share World requires the shared backend/access flow, which is not connected in this build yet. The World remains only on this PC."
            : "Manage access requires the shared backend/access flow, which is not connected in this build yet.";
    }

    private async Task RefreshUnifiedImportCandidatesAsync()
    {
        SetBusy(true);
        _gamePresentationCache.Clear();
        StatusText.Text = "Looking for installed games and local Worlds...";
        ImportDiscoveryText.Text = "Scanning registered game adapters...";

        try
        {
            var candidates = new List<UnifiedImportCandidate>();
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
                        candidates.Add(new UnifiedImportCandidate(
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

            var ordered = candidates
                .OrderBy(candidate => candidate.GameName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(candidate => candidate.Name, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            ImportCandidateComboBox.ItemsSource = ordered;
            ImportCandidateComboBox.SelectedIndex = ordered.Length > 0 ? 0 : -1;

            if (installedGameCount == 0)
            {
                ImportDiscoveryText.Text = "No supported installed games were detected on this device.";
                StatusText.Text = "No supported game installation found.";
            }
            else if (ordered.Length == 0)
            {
                ImportDiscoveryText.Text =
                    $"{installedGameCount} supported game installation(s) found, but no importable Worlds were detected.";
                StatusText.Text = "No importable Worlds found.";
            }
            else
            {
                ImportDiscoveryText.Text = ordered.Length == 1
                    ? "1 importable World found."
                    : $"{ordered.Length} importable Worlds found across {installedGameCount} installed games.";
                StatusText.Text = ImportDiscoveryText.Text;
            }
        }
        catch (Exception exception)
        {
            ImportCandidateComboBox.ItemsSource = null;
            ImportDiscoveryText.Text = "Could not scan installed games and Worlds.";
            StatusText.Text = "World discovery failed.";
            ShowError("Could not discover local Worlds", exception);
        }
        finally
        {
            SetBusy(false);
            UpdateUnifiedImportActionState();
        }
    }

    private async Task RefreshUnifiedWorldsAsync(
        WorldId? preferredWorldId = null,
        bool preserveStatus = false)
    {
        SetBusy(true);
        if (!preserveStatus)
        {
            StatusText.Text = "Loading Worlds...";
        }

        try
        {
            var worlds = await _storage.ListWorldsAsync();
            var items = new List<UnifiedWorldListItem>(worlds.Count);

            foreach (var world in worlds)
            {
                var gameVersion = "unknown";
                if (world.CurrentEnvironmentRevisionId is { } environmentRevisionId)
                {
                    var environment = await _storage.LoadEnvironmentRevisionAsync(
                        world.Id,
                        environmentRevisionId);
                    if (!string.IsNullOrWhiteSpace(environment?.Manifest.GameVersion))
                    {
                        gameVersion = environment.Manifest.GameVersion;
                    }
                }

                var presentation = await GetGamePresentationAsync(world.GameAdapterId);
                items.Add(new UnifiedWorldListItem(
                    world,
                    world.Name,
                    $"{FormatSharingMode(world.SharingMode)}  •  {gameVersion}",
                    gameVersion,
                    presentation.GameName,
                    presentation.IconPath));
            }

            var ordered = items
                .OrderBy(item => item.GameName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
            var view = new ListCollectionView(ordered);
            view.GroupDescriptions.Add(
                new PropertyGroupDescription(nameof(UnifiedWorldListItem.GameName)));
            view.SortDescriptions.Add(
                new SortDescription(nameof(UnifiedWorldListItem.GameName), ListSortDirection.Ascending));
            view.SortDescriptions.Add(
                new SortDescription(nameof(UnifiedWorldListItem.Name), ListSortDirection.Ascending));

            WorldList.ItemsSource = view;

            if (ordered.Count == 0)
            {
                _selectedWorld = null;
                EmptyStateText.Text =
                    "No managed Worlds yet. Use Import to turn a detected save into a private World.";
                EmptyStateText.Visibility = Visibility.Visible;
                WorldDetailsPanel.Visibility = Visibility.Collapsed;
            }
            else
            {
                var selection = preferredWorldId is { } wanted
                    ? ordered.FirstOrDefault(item => item.World.Id == wanted)
                    : ordered.FirstOrDefault(item => item.World.Id == _selectedWorld?.Id);
                WorldList.SelectedItem = selection ?? ordered[0];
            }

            if (!preserveStatus)
            {
                var gameCount = ordered
                    .Select(item => item.GameName)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Count();
                StatusText.Text = ordered.Count == 1
                    ? "1 managed World"
                    : $"{ordered.Count} managed Worlds across {gameCount} games";
            }
        }
        catch (Exception exception)
        {
            StatusText.Text = "Could not load Worlds.";
            ShowError("Could not load Worlds", exception);
        }
        finally
        {
            SetBusy(false);
            UpdateUnifiedActionState();
        }
    }

    private async Task RunUnifiedOperationAsync(string status, Func<Task> operation)
    {
        try
        {
            await RunOperationAsync(status, operation);
        }
        finally
        {
            UpdateUnifiedActionState();
            UpdateUnifiedImportActionState();
        }
    }

    private async Task<GameInstallation> GetGameInstallationAsync(IGameAdapter adapter)
    {
        var installation = (await adapter.DiscoverInstallationsAsync()).FirstOrDefault();
        return installation
            ?? throw new InvalidOperationException(
                $"{adapter.DisplayName} installation not found on this device.");
    }

    private async Task<GamePresentation> GetGamePresentationAsync(string adapterId)
    {
        if (_gamePresentationCache.TryGetValue(adapterId, out var cached))
        {
            return cached;
        }

        if (!TryGetAdapter(adapterId, out var adapter))
        {
            return new GamePresentation(adapterId, null);
        }

        var installation = (await adapter.DiscoverInstallationsAsync()).FirstOrDefault();
        return GetGamePresentation(adapter, installation);
    }

    private GamePresentation GetGamePresentation(
        IGameAdapter adapter,
        GameInstallation? installation)
    {
        if (_gamePresentationCache.TryGetValue(adapter.Id, out var cached))
        {
            return cached;
        }

        var presentation = new GamePresentation(
            adapter.DisplayName,
            installation is null ? null : _gameIconResolver.Resolve(installation));
        _gamePresentationCache[adapter.Id] = presentation;
        return presentation;
    }

    private bool TryGetAdapter(string adapterId, out IGameAdapter adapter)
    {
        if (_registeredGameAdapters.TryGetValue(adapterId, out var registered))
        {
            adapter = registered;
            return true;
        }

        adapter = null!;
        return false;
    }

    private void UpdateUnifiedActionState()
    {
        var world = _selectedWorld;
        IGameAdapter? adapter = null;
        if (world is not null)
        {
            _registeredGameAdapters.TryGetValue(world.GameAdapterId, out adapter);
        }

        var canContinue = adapter?.Capabilities.HasFlag(
            GameAdapterCapabilities.AutomaticLocalLaunch) == true;
        var canHost = adapter?.Capabilities.HasFlag(
            GameAdapterCapabilities.AutomaticHostLaunch) == true;

        ContinueButton.Visibility = Visibility.Visible;
        HostButton.Visibility = Visibility.Visible;
        ContinueButton.IsEnabled = !_isBusy && canContinue;
        HostButton.IsEnabled = !_isBusy &&
                               canHost &&
                               _deviceSettings.AllowHosting;

        ContinueButton.ToolTip = world is null
            ? "Select a World."
            : canContinue
                ? $"Continue this {adapter!.DisplayName} World."
                : $"{adapter?.DisplayName ?? world.GameAdapterId} does not support managed local launch yet.";

        HostButton.ToolTip = world is null
            ? "Select a World."
            : !canHost
                ? $"{adapter?.DisplayName ?? world.GameAdapterId} does not support managed hosting yet."
                : !_deviceSettings.AllowHosting
                    ? "Enable 'Allow this device to host' in Device settings first."
                    : $"Host this {adapter!.DisplayName} World temporarily on this device.";

        ShareButton.IsEnabled = !_isBusy && world is not null;
        ShareButton.Content = world?.SharingMode == WorldSharingMode.Shared
            ? "Manage access"
            : "Share World";
        ShareButton.ToolTip = world is null
            ? "Select a World."
            : world.SharingMode == WorldSharingMode.Shared
                ? "Manage access becomes functional when the shared backend/access flow is connected."
                : "Share World becomes functional when the shared backend/access flow is connected.";
    }

    private void UpdateUnifiedImportActionState()
        => ImportSelectedButton.IsEnabled =
            !_isBusy && ImportCandidateComboBox.SelectedItem is UnifiedImportCandidate;

    private static DataTemplate CreateImportCandidateTemplate()
    {
        var root = new FrameworkElementFactory(typeof(DockPanel));
        root.SetValue(FrameworkElement.MarginProperty, new Thickness(2, 4, 2, 4));
        root.SetValue(DockPanel.LastChildFillProperty, true);

        var icon = new FrameworkElementFactory(typeof(Image));
        icon.SetValue(FrameworkElement.WidthProperty, 28d);
        icon.SetValue(FrameworkElement.HeightProperty, 28d);
        icon.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 0, 8, 0));
        icon.SetValue(Image.StretchProperty, Stretch.Uniform);
        icon.SetValue(DockPanel.DockProperty, Dock.Left);
        icon.SetBinding(Image.SourceProperty, new Binding(nameof(UnifiedImportCandidate.GameIconPath)));
        root.AppendChild(icon);

        var text = new FrameworkElementFactory(typeof(StackPanel));
        var name = new FrameworkElementFactory(typeof(TextBlock));
        name.SetValue(TextBlock.FontWeightProperty, FontWeights.SemiBold);
        name.SetBinding(TextBlock.TextProperty, new Binding(nameof(UnifiedImportCandidate.Name)));
        text.AppendChild(name);

        var subtitle = new FrameworkElementFactory(typeof(TextBlock));
        subtitle.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 2, 0, 0));
        subtitle.SetValue(TextBlock.FontSizeProperty, 11d);
        subtitle.SetValue(TextBlock.TextWrappingProperty, TextWrapping.Wrap);
        subtitle.SetValue(UIElement.OpacityProperty, 0.72d);
        subtitle.SetBinding(TextBlock.TextProperty, new Binding(nameof(UnifiedImportCandidate.Subtitle)));
        text.AppendChild(subtitle);
        root.AppendChild(text);

        return new DataTemplate { VisualTree = root };
    }

    private static DataTemplate CreateWorldItemTemplate()
    {
        var root = new FrameworkElementFactory(typeof(DockPanel));
        root.SetValue(DockPanel.LastChildFillProperty, true);

        var icon = new FrameworkElementFactory(typeof(Image));
        icon.SetValue(FrameworkElement.WidthProperty, 40d);
        icon.SetValue(FrameworkElement.HeightProperty, 40d);
        icon.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 0, 8, 0));
        icon.SetValue(Image.StretchProperty, Stretch.Uniform);
        icon.SetValue(DockPanel.DockProperty, Dock.Left);
        icon.SetBinding(Image.SourceProperty, new Binding(nameof(UnifiedWorldListItem.GameIconPath)));
        root.AppendChild(icon);

        var text = new FrameworkElementFactory(typeof(StackPanel));
        var name = new FrameworkElementFactory(typeof(TextBlock));
        name.SetValue(TextBlock.FontSizeProperty, 15d);
        name.SetValue(TextBlock.FontWeightProperty, FontWeights.SemiBold);
        name.SetBinding(TextBlock.TextProperty, new Binding(nameof(UnifiedWorldListItem.Name)));
        text.AppendChild(name);

        var subtitle = new FrameworkElementFactory(typeof(TextBlock));
        subtitle.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 5, 0, 0));
        subtitle.SetValue(TextBlock.FontSizeProperty, 12d);
        subtitle.SetValue(TextBlock.TextWrappingProperty, TextWrapping.Wrap);
        subtitle.SetValue(UIElement.OpacityProperty, 0.72d);
        subtitle.SetBinding(TextBlock.TextProperty, new Binding(nameof(UnifiedWorldListItem.Subtitle)));
        text.AppendChild(subtitle);
        root.AppendChild(text);

        return new DataTemplate { VisualTree = root };
    }

    private static GroupStyle CreateGameGroupStyle()
    {
        var root = new FrameworkElementFactory(typeof(StackPanel));
        root.SetValue(StackPanel.OrientationProperty, Orientation.Horizontal);
        root.SetValue(FrameworkElement.MarginProperty, new Thickness(2, 16, 2, 8));

        var icon = new FrameworkElementFactory(typeof(Image));
        icon.SetValue(FrameworkElement.WidthProperty, 24d);
        icon.SetValue(FrameworkElement.HeightProperty, 24d);
        icon.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 0, 8, 0));
        icon.SetValue(Image.StretchProperty, Stretch.Uniform);
        icon.SetBinding(Image.SourceProperty, new Binding("Items[0].GameIconPath"));
        root.AppendChild(icon);

        var name = new FrameworkElementFactory(typeof(TextBlock));
        name.SetValue(TextBlock.FontSizeProperty, 15d);
        name.SetValue(TextBlock.FontWeightProperty, FontWeights.SemiBold);
        name.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        name.SetBinding(TextBlock.TextProperty, new Binding("Name"));
        root.AppendChild(name);

        var count = new FrameworkElementFactory(typeof(TextBlock));
        count.SetValue(FrameworkElement.MarginProperty, new Thickness(8, 0, 0, 0));
        count.SetValue(TextBlock.FontSizeProperty, 11d);
        count.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        count.SetValue(UIElement.OpacityProperty, 0.72d);
        count.SetBinding(
            TextBlock.TextProperty,
            new Binding("ItemCount") { StringFormat = "{0} Worlds" });
        root.AppendChild(count);

        return new GroupStyle
        {
            HeaderTemplate = new DataTemplate { VisualTree = root }
        };
    }

    private sealed record GamePresentation(string GameName, string? IconPath);

    private sealed record UnifiedWorldListItem(
        World World,
        string Name,
        string Subtitle,
        string GameVersion,
        string GameName,
        string? GameIconPath);

    private sealed record UnifiedImportCandidate(
        IGameAdapter Adapter,
        GameInstallation Installation,
        DetectedWorld World,
        string Name,
        string Subtitle,
        string GameName,
        string? GameIconPath);
}
