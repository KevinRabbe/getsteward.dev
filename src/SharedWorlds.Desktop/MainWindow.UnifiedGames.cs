using System.ComponentModel;
using System.Windows;
using System.Windows.Automation;
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

    internal async Task InitializeUnifiedGameUiAsync()
    {
        if (_unifiedGameUiInitialized)
        {
            return;
        }

        _unifiedGameUiInitialized = true;

        RefreshButton.Click += UnifiedRefreshButton_Click;
        ContinueButton.Click += UnifiedContinueButton_Click;
        HostButton.Click += UnifiedHostButton_Click;

        WorldList.SelectionChanged += UnifiedWorldList_SelectionChanged;
        WorldList.IsEnabledChanged += (_, _) => UpdateUnifiedActionState();
        AllowHostingCheckBox.Click += (_, _) => UpdateUnifiedActionState();

        WorldList.ItemTemplate = CreateWorldItemTemplate();
        WorldList.GroupStyle.Clear();
        WorldList.GroupStyle.Add(CreateGameGroupStyle());

        ContinueButton.Visibility = Visibility.Visible;
        HostButton.Visibility = Visibility.Visible;

        // WorldSharing owns this action. Keep it inert until that controller initializes rather than
        // maintaining a second placeholder state machine here.
        ShareButton.IsEnabled = false;

        await RefreshUnifiedWorldsAsync();
    }

    private async void UnifiedRefreshButton_Click(object sender, RoutedEventArgs e)
        => await RefreshUnifiedWorldsAsync(_selectedWorld?.Id);

    private void UnifiedWorldList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (WorldList.SelectedItem is not UnifiedWorldListItem selected)
        {
            _selectedWorld = null;
            EmptyStateText.Visibility = Visibility.Visible;
            WorldDetailsPanel.Visibility = Visibility.Collapsed;
            UpdateUnifiedActionState();
            UpdateResponsibilityPresentation();
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
        UpdateResponsibilityPresentation();
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
                    $"{adapter.DisplayName} is running. End the game/server session normally; Steward will then capture and commit the new canonical revision.";

                var lifecycle = GetLifecycleForWorld(world);
                var updated = await lifecycle.ContinueAsHostAsync(
                    world.Id,
                    adapter,
                    installation,
                    GetUserForWorld(world));

                StatusText.Text =
                    $"Hosted World '{updated.Name}' committed as revision {updated.CurrentStateRevisionId}.";
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
                EmptyStateText.Text = _lastRemoteWorldLoadError is null
                    ? "No managed Worlds yet. Use Import to turn a detected save into a private World."
                    : "No local Worlds are managed on this PC. Shared Worlds are temporarily unavailable.";
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
                if (_lastRemoteWorldLoadError is not null)
                {
                    StatusText.Text = "Local Worlds loaded. Shared Worlds are temporarily unavailable.";
                }
                else
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
}
