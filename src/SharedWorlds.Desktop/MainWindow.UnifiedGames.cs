using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using SharedWorlds.Core.Abstractions;
using SharedWorlds.Core.Domain;

namespace SharedWorlds.Desktop;

public partial class MainWindow
{
    private readonly IReadOnlyDictionary<string, IGameAdapter> _registeredGameAdapters =
        DesktopGameAdapterCatalog.Create();

    private readonly GameIconResolver _gameIconResolver = new();
    private readonly Dictionary<string, GamePresentation> _gamePresentationCache =
        new(StringComparer.Ordinal);
    private IReadOnlyList<UnifiedWorldListItem> _allWorldItems = Array.Empty<UnifiedWorldListItem>();
    private string? _selectedGameAdapterId;
    private bool _unifiedGameUiInitialized;

    internal async Task InitializeUnifiedGameUiAsync()
    {
        if (_unifiedGameUiInitialized)
        {
            return;
        }

        _unifiedGameUiInitialized = true;
        RefreshButton.Click += UnifiedRefreshButton_Click;
        RefreshGameButton.Click += UnifiedRefreshButton_Click;
        ContinueButton.Click += UnifiedContinueButton_Click;
        HostButton.Click += UnifiedHostButton_Click;
        GameLibraryList.SelectionChanged += UnifiedGameLibraryList_SelectionChanged;
        WorldList.SelectionChanged += UnifiedWorldList_SelectionChanged;
        WorldList.IsEnabledChanged += (_, _) => UpdateUnifiedActionState();
        AllowHostingCheckBox.Click += (_, _) => UpdateUnifiedActionState();
        BackToGamesButton.Click += UnifiedBackToGamesButton_Click;
        BackToWorldsButton.Click += UnifiedBackToWorldsButton_Click;
        SizeChanged += UnifiedMainWindow_SizeChanged;

        WorldList.ItemTemplate = CreateWorldItemTemplate();
        WorldList.GroupStyle.Clear();

        ContinueButton.Visibility = Visibility.Visible;
        HostButton.Visibility = Visibility.Visible;

        // WorldSharing owns this action. Keep it inert until that controller initializes rather than
        // maintaining a second placeholder state machine here.
        ShareButton.IsEnabled = false;

        ShowGamesLibrary(focusLibrary: false);
        await RefreshUnifiedWorldsAsync();
    }

    private async void UnifiedRefreshButton_Click(object sender, RoutedEventArgs e)
        => await RefreshUnifiedWorldsAsync(_selectedWorld?.Id);

    private void UnifiedGameLibraryList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (GameLibraryList.SelectedItem is not GameLibraryItem selected)
        {
            return;
        }

        OpenGameWorkspace(selected.AdapterId, selected.GameName, preferredWorldId: null);
    }

    private void UnifiedWorldList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (WorldList.SelectedItem is not UnifiedWorldListItem selected)
        {
            _selectedWorld = null;
            SetGameWorkspaceEmptyState();
            UpdateUnifiedActionState();
            UpdateResponsibilityPresentation();
            ApplyGameNavigationLayout();
            return;
        }

        _selectedWorld = selected.World;
        _selectedGameAdapterId = selected.AdapterId;
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
        UpdateResponsibilityPresentation();
        ApplyGameNavigationLayout();
    }

    private void UnifiedBackToGamesButton_Click(object sender, RoutedEventArgs e)
        => ShowGamesLibrary(focusLibrary: true);

    private void UnifiedBackToWorldsButton_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedGameAdapterId is null)
        {
            ShowGamesLibrary(focusLibrary: true);
            return;
        }

        _selectedWorld = null;
        WorldList.SelectedItem = null;
        SetGameWorkspaceEmptyState();
        UpdateUnifiedActionState();
        UpdateResponsibilityPresentation();
        ApplyGameNavigationLayout();
        WorldList.Focus();
    }

    private void UnifiedMainWindow_SizeChanged(object sender, SizeChangedEventArgs e)
        => ApplyGameNavigationLayout();

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

        if (!IsSelectedWorldEnvironmentReadyForPlay())
        {
            StatusText.Text =
                $"Verify the exact environment for shared World '{world.Name}' before starting it.";
            return;
        }

        await RunUnifiedOperationAsync(
            $"Starting {world.Name}...",
            async () =>
            {
                var installation = await GetGameInstallationAsync(adapter);
                var lifecycle = GetLifecycleForWorld(world);
                var updated = await lifecycle.ContinueLocalAsync(
                    world.Id,
                    adapter,
                    installation,
                    GetUserForWorld(world));

                StatusText.Text = $"Saved '{updated.Name}'.";
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

        if (!IsSelectedWorldEnvironmentReadyForPlay())
        {
            StatusText.Text =
                $"Verify the exact environment for shared World '{world.Name}' before hosting it.";
            return;
        }

        await RunUnifiedOperationAsync(
            $"Hosting {world.Name}...",
            async () =>
            {
                var installation = await GetGameInstallationAsync(adapter);
                StatusText.Text =
                    $"{adapter.DisplayName} is running. When the hosted session ends, Steward will save the updated World.";
                var lifecycle = GetLifecycleForWorld(world);
                var hostAdapter = _remoteWorldIds.Contains(world.Id) && _remoteRuntime is { } remoteRuntime
                    ? remoteRuntime.CoordinateManagedHost(world.Id, adapter)
                    : adapter;
                var updated = await lifecycle.ContinueAsHostAsync(
                    world.Id,
                    hostAdapter,
                    installation,
                    GetUserForWorld(world));
                StatusText.Text = $"Hosted session finished. Saved '{updated.Name}'.";
                await RefreshUnifiedWorldsAsync(updated.Id, preserveStatus: true);
            });
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
            var worlds = await ListDesktopWorldsAsync();
            var items = new List<UnifiedWorldListItem>(worlds.Count);

            foreach (var world in worlds)
            {
                var gameVersion = "unknown";
                if (world.CurrentEnvironmentRevisionId is { } environmentRevisionId)
                {
                    var environment = await LoadEnvironmentRevisionForWorldAsync(
                        world,
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
                    world.GameAdapterId,
                    presentation.GameName,
                    presentation.IconPath));
            }

            var ordered = items
                .OrderBy(item => item.GameName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
            _allWorldItems = ordered;

            await RefreshGameLibraryAsync();

            var selection = preferredWorldId is { } wanted
                ? ordered.FirstOrDefault(item => item.World.Id == wanted)
                : ordered.FirstOrDefault(item => item.World.Id == _selectedWorld?.Id);

            if (selection is not null)
            {
                OpenGameWorkspace(selection.AdapterId, selection.GameName, selection.World.Id);
            }
            else if (_selectedGameAdapterId is { } selectedAdapterId &&
                     TryGetGameLibraryItem(selectedAdapterId, out var selectedGame))
            {
                OpenGameWorkspace(selectedAdapterId, selectedGame.GameName, preferredWorldId: null);
            }
            else
            {
                ShowGamesLibrary(focusLibrary: false);
            }

            if (!preserveStatus)
            {
                if (_lastRemoteWorldLoadError is not null)
                {
                    StatusText.Text = "Local Worlds loaded. Shared Worlds are temporarily unavailable.";
                }
                else
                {
                    var managedGameCount = ordered
                        .Select(item => item.AdapterId)
                        .Distinct(StringComparer.Ordinal)
                        .Count();
                    StatusText.Text = ordered.Count switch
                    {
                        0 => $"{_registeredGameAdapters.Count} supported games • no managed Worlds yet",
                        1 => "1 managed World",
                        _ => $"{ordered.Count} managed Worlds across {managedGameCount} games"
                    };
                }
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
            UpdateResponsibilityPresentation();
        }
    }

    private async Task RefreshGameLibraryAsync()
    {
        var games = new List<GameLibraryItem>(_registeredGameAdapters.Count);
        foreach (var adapter in _registeredGameAdapters.Values
                     .OrderBy(adapter => adapter.DisplayName, StringComparer.OrdinalIgnoreCase))
        {
            var presentation = await GetGamePresentationAsync(adapter.Id);
            var worldCount = _allWorldItems.Count(item =>
                string.Equals(item.AdapterId, adapter.Id, StringComparison.Ordinal));
            var summary = worldCount switch
            {
                0 => "No managed Worlds yet",
                1 => "1 managed World",
                _ => $"{worldCount} managed Worlds"
            };
            games.Add(new GameLibraryItem(
                adapter.Id,
                presentation.GameName,
                summary,
                presentation.IconPath));
        }

        GameLibraryList.ItemsSource = games;
    }

    private bool TryGetGameLibraryItem(string adapterId, out GameLibraryItem item)
    {
        if (GameLibraryList.ItemsSource is IEnumerable<GameLibraryItem> games)
        {
            var match = games.FirstOrDefault(game =>
                string.Equals(game.AdapterId, adapterId, StringComparison.Ordinal));
            if (match is not null)
            {
                item = match;
                return true;
            }
        }

        item = null!;
        return false;
    }

    private void OpenGameWorkspace(
        string adapterId,
        string gameName,
        WorldId? preferredWorldId)
    {
        _selectedGameAdapterId = adapterId;
        SelectedGameNameText.Text = gameName;
        GamesLibraryPanel.Visibility = Visibility.Collapsed;

        var gameWorlds = _allWorldItems
            .Where(item => string.Equals(item.AdapterId, adapterId, StringComparison.Ordinal))
            .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        WorldList.ItemsSource = gameWorlds;

        var selection = preferredWorldId is { } wanted
            ? gameWorlds.FirstOrDefault(item => item.World.Id == wanted)
            : null;
        WorldList.SelectedItem = selection;

        if (selection is null)
        {
            _selectedWorld = null;
            SetGameWorkspaceEmptyState();
            UpdateUnifiedActionState();
            UpdateResponsibilityPresentation();
        }

        ApplyGameNavigationLayout();
    }

    private void ShowGamesLibrary(bool focusLibrary)
    {
        _selectedGameAdapterId = null;
        _selectedWorld = null;
        WorldList.SelectedItem = null;
        WorldList.ItemsSource = Array.Empty<UnifiedWorldListItem>();
        GameLibraryList.SelectedItem = null;
        EmptyStateText.Text = "Select a World.";
        EmptyStateText.Visibility = Visibility.Visible;
        WorldDetailsPanel.Visibility = Visibility.Collapsed;
        UpdateUnifiedActionState();
        UpdateResponsibilityPresentation();
        ApplyGameNavigationLayout();

        if (focusLibrary)
        {
            GameLibraryList.Focus();
        }
    }

    private void SetGameWorkspaceEmptyState()
    {
        if (_selectedGameAdapterId is null)
        {
            EmptyStateText.Text = "Select a World.";
        }
        else if (WorldList.Items.Count == 0)
        {
            EmptyStateText.Text = _lastRemoteWorldLoadError is null
                ? $"No managed {SelectedGameNameText.Text} Worlds yet. Return to Games to Import one."
                : $"No local {SelectedGameNameText.Text} Worlds are managed on this PC. Shared Worlds are temporarily unavailable.";
        }
        else
        {
            EmptyStateText.Text = "Select a World.";
        }

        EmptyStateText.Visibility = Visibility.Visible;
        WorldDetailsPanel.Visibility = Visibility.Collapsed;
    }

    private void ApplyGameNavigationLayout()
    {
        if (_selectedGameAdapterId is null)
        {
            GamesLibraryPanel.Visibility = Visibility.Visible;
            WorldSidebar.Visibility = Visibility.Collapsed;
            WorldDetailsScroll.Visibility = Visibility.Collapsed;
            BackToWorldsButton.Visibility = Visibility.Collapsed;
            return;
        }

        GamesLibraryPanel.Visibility = Visibility.Collapsed;
        var narrow = ActualWidth < 860;
        if (narrow && _selectedWorld is not null)
        {
            WorldSidebar.Visibility = Visibility.Collapsed;
            WorldDetailsScroll.Visibility = Visibility.Visible;
            BackToWorldsButton.Visibility = Visibility.Visible;
            return;
        }

        WorldSidebar.Visibility = Visibility.Visible;
        WorldDetailsScroll.Visibility = narrow ? Visibility.Collapsed : Visibility.Visible;
        BackToWorldsButton.Visibility = Visibility.Collapsed;
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
            UpdateResponsibilityPresentation();
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

        var canStart = adapter?.Capabilities.HasFlag(
            GameAdapterCapabilities.AutomaticLocalLaunch) == true;
        var canHost = adapter?.Capabilities.HasFlag(
            GameAdapterCapabilities.AutomaticHostLaunch) == true;
        var environmentReady = IsSelectedWorldEnvironmentReadyForPlay();

        ContinueButton.Visibility = Visibility.Visible;
        HostButton.Visibility = Visibility.Visible;
        ContinueButton.IsEnabled = !_isBusy && canStart && environmentReady;
        HostButton.IsEnabled = !_isBusy &&
                               canHost &&
                               _deviceSettings.AllowHosting &&
                               environmentReady;

        var continueHelp = world is null
            ? "Select a World."
            : !canStart
                ? $"{adapter?.DisplayName ?? world.GameAdapterId} does not support managed local launch yet."
                : !environmentReady
                    ? "Run Verify Environment and reach Ready before starting this shared World."
                    : $"Start this {adapter!.DisplayName} World on this device.";
        ContinueButton.ToolTip = continueHelp;
        AutomationProperties.SetHelpText(ContinueButton, continueHelp);

        var hostHelp = world is null
            ? "Select a World."
            : !canHost
                ? $"{adapter?.DisplayName ?? world.GameAdapterId} does not support managed hosting yet."
                : !_deviceSettings.AllowHosting
                    ? "Enable 'Allow this device to host' in Device settings first."
                    : !environmentReady
                        ? "Run Verify Environment and reach Ready before hosting this shared World."
                        : $"Host this {adapter!.DisplayName} World temporarily on this device.";
        HostButton.ToolTip = hostHelp;
        AutomationProperties.SetHelpText(HostButton, hostHelp);
    }

    private void UpdateUnifiedImportActionState()
        => UpdateImportBrowserActionState();

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
        icon.SetBinding(Image.SourceProperty, new Binding(nameof(ImportBrowserCandidate.GameIconPath)));
        root.AppendChild(icon);

        var text = new FrameworkElementFactory(typeof(StackPanel));
        var name = new FrameworkElementFactory(typeof(TextBlock));
        name.SetValue(TextBlock.FontWeightProperty, FontWeights.SemiBold);
        name.SetBinding(TextBlock.TextProperty, new Binding(nameof(ImportBrowserCandidate.Name)));
        text.AppendChild(name);

        var subtitle = new FrameworkElementFactory(typeof(TextBlock));
        subtitle.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 2, 0, 0));
        subtitle.SetValue(TextBlock.FontSizeProperty, 11d);
        subtitle.SetValue(TextBlock.TextWrappingProperty, TextWrapping.Wrap);
        subtitle.SetBinding(TextBlock.TextProperty, new Binding(nameof(ImportBrowserCandidate.Subtitle)));
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
        subtitle.SetBinding(TextBlock.TextProperty, new Binding(nameof(UnifiedWorldListItem.Subtitle)));
        text.AppendChild(subtitle);
        root.AppendChild(text);

        return new DataTemplate { VisualTree = root };
    }

    private sealed record GamePresentation(string GameName, string? IconPath);

    private sealed record GameLibraryItem(
        string AdapterId,
        string GameName,
        string Summary,
        string? IconPath);

    private sealed record UnifiedWorldListItem(
        World World,
        string Name,
        string Subtitle,
        string GameVersion,
        string AdapterId,
        string GameName,
        string? GameIconPath);
}