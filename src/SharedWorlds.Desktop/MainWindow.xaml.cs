using System.IO;
using System.Text.Json;
using System.Windows;
using SharedWorlds.Core.Abstractions;
using SharedWorlds.Core.Domain;
using SharedWorlds.Core.Worlds;
using SharedWorlds.Infrastructure.Sessions;
using SharedWorlds.Infrastructure.Storage;

namespace SharedWorlds.Desktop;

public partial class MainWindow : Window
{
    private readonly IWorldStorage _storage;
    private readonly LocalWorldSessionCoordinator _localSessionCoordinator;
    private readonly ManagedWritableSessionGate _localManagedSessionGate;
    private readonly WorldLifecycleService _lifecycle;
    private readonly DeviceSettingsStore _deviceSettingsStore;

    private World? _selectedWorld;
    private DeviceSettings _deviceSettings = DeviceSettingsStore.CreateInitial(hasManagedWorlds: false);
    private bool _deviceSettingsUsableForRemote;
    private bool _isBusy;

    public MainWindow()
    {
        InitializeComponent();

        // Keep the existing local data root for persistence compatibility while the product shell
        // moves from the old SharedWorlds working name to Steward.
        var sharedWorldsRoot = Path.Combine(GetLocalDataRoot(), "SharedWorlds");
        var storageRoot = Path.Combine(sharedWorldsRoot, "data");
        _storage = new LocalWorldStorage(storageRoot);
        _workspaceRecoveryStore = new LocalWorkspaceRecoveryStore(storageRoot);
        _localSessionCoordinator = new LocalWorldSessionCoordinator();
        _localManagedSessionGate = new ManagedWritableSessionGate();
        _lifecycle = new WorldLifecycleService(
            _storage,
            _localSessionCoordinator,
            _workspaceRecoveryStore,
            _localManagedSessionGate,
            CreateDesktopLifecycleObserver());
        _deviceSettingsStore = new DeviceSettingsStore(
            Path.Combine(sharedWorldsRoot, "settings", "device.json"));

        Closed += (_, _) => DisposeRemoteRuntime();
        InitializeTray();
    }

    private async Task LoadDeviceSettingsAsync()
    {
        _deviceSettingsUsableForRemote = false;
        try
        {
            var worlds = await _storage.ListWorldsAsync();
            _deviceSettings = await _deviceSettingsStore.LoadOrCreateAsync(
                hasManagedWorlds: worlds.Count > 0);
            _deviceSettingsUsableForRemote = true;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or JsonException)
        {
            // The fallback keeps local UI behavior usable, but its generated installation ID is not
            // durable. Never use it for installation-bound remote identity/authority.
            _deviceSettings = DeviceSettingsStore.CreateInitial(hasManagedWorlds: false);
            ShowError(
                "Could not load device settings",
                new InvalidOperationException(
                    "Steward kept hosting and shared Worlds disabled on this device because its durable device settings could not be loaded.",
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
            _deviceSettingsUsableForRemote = true;
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
        AllowHostingCheckBox.IsEnabled = !isBusy;
        WorldList.IsEnabled = !isBusy;
        UpdateUnifiedActionState();
        UpdateUnifiedImportActionState();
        UpdateWorldVersionPolicyUi();
        UpdateEnvironmentReadinessUi();
        UpdateResponsibilityPresentation();
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
