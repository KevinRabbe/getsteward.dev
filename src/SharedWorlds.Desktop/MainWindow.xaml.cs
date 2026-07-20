using System.Windows;
using System.Windows.Controls;
using SharedWorlds.Core.Abstractions;
using SharedWorlds.Core.Domain;
using SharedWorlds.Core.Worlds;
using SharedWorlds.GameAdapters.Factorio;
using SharedWorlds.Infrastructure.Sessions;
using SharedWorlds.Infrastructure.Storage;

namespace SharedWorlds.Desktop;

public partial class MainWindow : Window
{
    private readonly IWorldStorage _storage;
    private readonly WorldLifecycleService _lifecycle;
    private readonly FactorioAdapter _factorioAdapter = new();

    private World? _selectedWorld;
    private bool _isBusy;

    public MainWindow()
    {
        InitializeComponent();

        var storageRoot = Path.Combine(GetLocalDataRoot(), "SharedWorlds", "data");
        _storage = new LocalWorldStorage(storageRoot);
        _lifecycle = new WorldLifecycleService(
            _storage,
            new LocalWorldSessionCoordinator(),
            new LocalWorkspaceRecoveryStore(storageRoot));

        Loaded += MainWindow_Loaded;
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
        => await RefreshWorldsAsync();

    private async void RefreshButton_Click(object sender, RoutedEventArgs e)
        => await RefreshWorldsAsync(_selectedWorld?.Id);

    private void WorldList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (WorldList.SelectedItem is not WorldListItem selected)
        {
            _selectedWorld = null;
            EmptyStateText.Visibility = Visibility.Visible;
            WorldDetailsPanel.Visibility = Visibility.Collapsed;
            UpdateActionState();
            return;
        }

        _selectedWorld = selected.World;
        EmptyStateText.Visibility = Visibility.Collapsed;
        WorldDetailsPanel.Visibility = Visibility.Visible;

        WorldNameText.Text = selected.World.Name;
        GameText.Text = GetGameDisplayName(selected.World.GameAdapterId);
        SharingText.Text = FormatSharingMode(selected.World.SharingMode);
        VersionText.Text = $"Version {selected.GameVersion}";
        WorldIdText.Text = selected.World.Id.ToString();
        EnvironmentRevisionText.Text = selected.World.CurrentEnvironmentRevisionId?.ToString() ?? "none";
        StateRevisionText.Text = selected.World.CurrentStateRevisionId?.ToString() ?? "none";

        UpdateActionState();
    }

    private async void ContinueButton_Click(object sender, RoutedEventArgs e)
    {
        var world = _selectedWorld;
        if (world is null)
        {
            return;
        }

        await RunOperationAsync(
            $"Starting {world.Name}...",
            async () =>
            {
                EnsureFactorioWorld(world);
                var installation = await GetFactorioInstallationAsync();
                var updated = await _lifecycle.ContinueLocalAsync(
                    world.Id,
                    _factorioAdapter,
                    installation,
                    GetLocalUser());

                StatusText.Text = $"World '{updated.Name}' committed as revision {updated.CurrentStateRevisionId}.";
                await RefreshWorldsAsync(updated.Id, preserveStatus: true);
            });
    }

    private async void HostButton_Click(object sender, RoutedEventArgs e)
    {
        var world = _selectedWorld;
        if (world is null)
        {
            return;
        }

        await RunOperationAsync(
            $"Hosting {world.Name}...",
            async () =>
            {
                EnsureFactorioWorld(world);
                var installation = await GetFactorioInstallationAsync();
                var updated = await _lifecycle.ContinueAsHostAsync(
                    world.Id,
                    _factorioAdapter,
                    installation,
                    GetLocalUser());

                StatusText.Text = $"Hosted World '{updated.Name}' committed as revision {updated.CurrentStateRevisionId}.";
                await RefreshWorldsAsync(updated.Id, preserveStatus: true);
            });
    }

    private async void ShareButton_Click(object sender, RoutedEventArgs e)
    {
        var world = _selectedWorld;
        if (world is null)
        {
            return;
        }

        var nextMode = world.SharingMode == WorldSharingMode.LocalOnly
            ? WorldSharingMode.Shared
            : WorldSharingMode.LocalOnly;

        await RunOperationAsync(
            nextMode == WorldSharingMode.Shared
                ? $"Sharing {world.Name}..."
                : $"Making {world.Name} local-only...",
            async () =>
            {
                var updated = await _lifecycle.SetSharingModeAsync(world.Id, nextMode);
                StatusText.Text = nextMode == WorldSharingMode.Shared
                    ? $"World '{updated.Name}' is now shared and eligible for Host / Join workflows."
                    : $"World '{updated.Name}' is now local-only.";
                await RefreshWorldsAsync(updated.Id, preserveStatus: true);
            });
    }

    private async Task RefreshWorldsAsync(
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
            var items = new List<WorldListItem>(worlds.Count);

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

                items.Add(new WorldListItem(
                    world,
                    world.Name,
                    $"{GetGameDisplayName(world.GameAdapterId)}  •  {FormatSharingMode(world.SharingMode)}  •  {gameVersion}",
                    gameVersion));
            }

            WorldList.ItemsSource = items;

            if (items.Count == 0)
            {
                _selectedWorld = null;
                EmptyStateText.Text = "No managed Worlds yet. Import remains available through the CLI while the desktop import flow is built.";
                EmptyStateText.Visibility = Visibility.Visible;
                WorldDetailsPanel.Visibility = Visibility.Collapsed;
            }
            else
            {
                var selection = preferredWorldId is { } wanted
                    ? items.FirstOrDefault(item => item.World.Id == wanted)
                    : items.FirstOrDefault(item => item.World.Id == _selectedWorld?.Id);
                WorldList.SelectedItem = selection ?? items[0];
            }

            if (!preserveStatus)
            {
                StatusText.Text = items.Count == 1
                    ? "1 managed World"
                    : $"{items.Count} managed Worlds";
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
        }
    }

    private async Task RunOperationAsync(string status, Func<Task> operation)
    {
        SetBusy(true);
        StatusText.Text = status;

        try
        {
            await operation();
        }
        catch (Exception exception)
        {
            StatusText.Text = "Operation failed.";
            ShowError("SharedWorlds operation failed", exception);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task<GameInstallation> GetFactorioInstallationAsync()
    {
        var installation = (await _factorioAdapter.DiscoverInstallationsAsync()).FirstOrDefault();
        return installation
            ?? throw new InvalidOperationException("Factorio installation not found on this device.");
    }

    private static void EnsureFactorioWorld(World world)
    {
        if (!string.Equals(world.GameAdapterId, "factorio", StringComparison.Ordinal))
        {
            throw new NotSupportedException(
                $"Desktop play actions are not wired for adapter '{world.GameAdapterId}' yet.");
        }
    }

    private void SetBusy(bool isBusy)
    {
        _isBusy = isBusy;
        RefreshButton.IsEnabled = !isBusy;
        WorldList.IsEnabled = !isBusy;
        UpdateActionState();
    }

    private void UpdateActionState()
    {
        var world = _selectedWorld;
        var isFactorio = world is not null &&
                         string.Equals(world.GameAdapterId, "factorio", StringComparison.Ordinal);

        ContinueButton.IsEnabled = !_isBusy && isFactorio;
        HostButton.IsEnabled = !_isBusy && isFactorio && world?.SharingMode == WorldSharingMode.Shared;
        ShareButton.IsEnabled = !_isBusy && world is not null;
        ShareButton.Content = world?.SharingMode == WorldSharingMode.Shared
            ? "Make Local Only"
            : "Share World";
    }

    private static string GetGameDisplayName(string adapterId)
        => string.Equals(adapterId, "factorio", StringComparison.Ordinal)
            ? "Factorio"
            : adapterId;

    private static string FormatSharingMode(WorldSharingMode sharingMode)
        => sharingMode == WorldSharingMode.LocalOnly ? "Local only" : "Shared";

    private static UserIdentity GetLocalUser()
        => new(
            Provider: "local",
            ExternalId: Environment.UserName,
            DisplayName: Environment.UserName);

    private static string GetLocalDataRoot()
    {
        var path = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return string.IsNullOrWhiteSpace(path) ? Path.GetTempPath() : path;
    }

    private static void ShowError(string title, Exception exception)
        => MessageBox.Show(
            exception.Message,
            title,
            MessageBoxButton.OK,
            MessageBoxImage.Error);

    private sealed record WorldListItem(
        World World,
        string Name,
        string Subtitle,
        string GameVersion);
}
