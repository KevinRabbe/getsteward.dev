using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using SharedWorlds.Core.Abstractions;
using SharedWorlds.Core.Domain;
using SharedWorlds.Core.Worlds;
using SharedWorlds.Infrastructure.Sessions;
using SharedWorlds.Infrastructure.Storage;

namespace SharedWorlds.Desktop;

public partial class MainWindow : Window
{
    private readonly IWorldStorage _storage;
    private readonly WorldLifecycleService _lifecycle;
    private readonly DeviceSettingsStore _deviceSettingsStore;

    private World? _selectedWorld;
    private DeviceSettings _deviceSettings = new(
        AllowHosting: false,
        HostingPreferenceExplicit: false);
    private bool _isBusy;

    public MainWindow()
    {
        InitializeComponent();

        var sharedWorldsRoot = Path.Combine(GetLocalDataRoot(), "SharedWorlds");
        var storageRoot = Path.Combine(sharedWorldsRoot, "data");
        _storage = new LocalWorldStorage(storageRoot);
        _workspaceRecoveryStore = new LocalWorkspaceRecoveryStore(storageRoot);
        _lifecycle = new WorldLifecycleService(
            _storage,
            new LocalWorldSessionCoordinator(),
            _workspaceRecoveryStore,
            new ManagedWritableSessionGate(),
            CreateDesktopLifecycleObserver());
        _deviceSettingsStore = new DeviceSettingsStore(
            Path.Combine(sharedWorldsRoot, "settings", "device.json"));

        InitializeTray();
    }

    // These handlers remain only because the current XAML still names them while UI-1 is replacing
    // the transitional shell. Unified startup removes/replaces them before the active product path is
    // used. They intentionally contain no legacy Factorio-specific behavior.
    private void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
    }

    private void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
    }

    private void OpenImportButton_Click(object sender, RoutedEventArgs e)
    {
    }

    private void ScanImportsButton_Click(object sender, RoutedEventArgs e)
    {
    }

    private void CancelImportButton_Click(object sender, RoutedEventArgs e)
        => ImportPanel.Visibility = Visibility.Collapsed;

    private void ImportCandidateComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
    }

    private void AllowHostingCheckBox_Click(object sender, RoutedEventArgs e)
    {
    }

    private void ImportSelectedButton_Click(object sender, RoutedEventArgs e)
    {
    }

    private void WorldList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
    }

    private void ContinueButton_Click(object sender, RoutedEventArgs e)
    {
    }

    private void HostButton_Click(object sender, RoutedEventArgs e)
    {
    }

    private void ShareButton_Click(object sender, RoutedEventArgs e)
    {
    }

    private async Task LoadDeviceSettingsAsync()
    {
        try
        {
            var worlds = await _storage.ListWorldsAsync();
            _deviceSettings = await _deviceSettingsStore.LoadOrCreateAsync(
                hasManagedWorlds: worlds.Count > 0);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or JsonException)
        {
            _deviceSettings = new DeviceSettings(
                AllowHosting: false,
                HostingPreferenceExplicit: false);
            ShowError(
                "Could not load device settings",
                new InvalidOperationException(
                    "Steward kept hosting disabled on this device because its local device settings could not be loaded.",
                    exception));
        }

        AllowHostingCheckBox.IsChecked = _deviceSettings.AllowHosting;
        UpdateHostingPreferenceText();
        UpdateUnifiedActionState();
    }

    private async Task TryEnableCreatorDeviceHostingAsync()
    {
        if (_deviceSettings.AllowHosting || _deviceSettings.HostingPreferenceExplicit)
        {
            return;
        }

        var updated = _deviceSettings with { AllowHosting = true };
        try
        {
            await _deviceSettingsStore.SaveAsync(updated);
            _deviceSettings = updated;
            AllowHostingCheckBox.IsChecked = true;
            UpdateHostingPreferenceText();
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or JsonException)
        {
            ShowError(
                "World imported, but hosting preference was not saved",
                new InvalidOperationException(
                    "The World was imported successfully. Enable 'Allow this device to host' manually if this device should host Worlds.",
                    exception));
        }
    }

    private async Task<string> CreateUniqueWorldNameAsync(string preferredName)
    {
        var baseName = string.IsNullOrWhiteSpace(preferredName) ? "Imported World" : preferredName.Trim();
        var worlds = await _storage.ListWorldsAsync();
        var existingNames = worlds
            .Select(world => world.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (!existingNames.Contains(baseName))
        {
            return baseName;
        }

        for (var suffix = 2; ; suffix++)
        {
            var candidate = $"{baseName} {suffix}";
            if (!existingNames.Contains(candidate))
            {
                return candidate;
            }
        }
    }

    private async Task RunOperationAsync(string status, Func<Task> operation)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(status);
        ArgumentNullException.ThrowIfNull(operation);

        SetBusy(true);
        StatusText.Text = status;

        try
        {
            await operation();
        }
        catch (Exception exception)
        {
            StatusText.Text = "Operation failed.";
            ShowError("Steward operation failed", exception);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void SetBusy(bool isBusy)
    {
        _isBusy = isBusy;
        RefreshButton.IsEnabled = !isBusy;
        OpenImportButton.IsEnabled = !isBusy;
        ScanImportsButton.IsEnabled = !isBusy;
        CancelImportButton.IsEnabled = !isBusy;
        ImportCandidateComboBox.IsEnabled = !isBusy;
        AllowHostingCheckBox.IsEnabled = !isBusy;
        WorldList.IsEnabled = !isBusy;
        UpdateUnifiedActionState();
        UpdateUnifiedImportActionState();
    }

    private void UpdateHostingPreferenceText()
        => HostingPreferenceText.Text = _deviceSettings.AllowHosting
            ? "This device may host Worlds."
            : "Hosting is disabled on this device.";

    private static string FormatSharingMode(WorldSharingMode sharingMode)
        => sharingMode == WorldSharingMode.LocalOnly ? "Only on this PC" : "Shared";

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
}
