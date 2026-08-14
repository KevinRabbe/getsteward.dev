using System.ComponentModel;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using SharedWorlds.Core.Abstractions;
using SharedWorlds.Core.Domain;
using SharedWorlds.Core.Storage;
using SharedWorlds.Core.Worlds;
using SharedWorlds.Infrastructure.Diagnostics;
using SharedWorlds.Infrastructure.Remote;
using SharedWorlds.Infrastructure.Sessions;
using SharedWorlds.Infrastructure.Storage;

namespace SharedWorlds.Desktop;

public partial class MainWindow : Window
{
    private readonly IWorldStorage _storage;
    private readonly string _storageRoot;
    private readonly object _ownedWorldLocationMigrationStateGate = new();
    private IOwnedWorldLocationPublicationJournal? _ownedWorldLocationPublicationJournal;
    private StewardOwnedWorldLocationPublicationTrigger? _ownedWorldLocationPublicationTrigger;
    private readonly SemaphoreSlim _ownedWorldLocationPublicationGate = new(1, 1);
    private readonly LocalWorldSessionCoordinator _localSessionCoordinator;
    private readonly ManagedWritableSessionGate _localManagedSessionGate;
    private readonly WorldLifecycleService _lifecycle;
    private readonly DeviceSettingsStore _deviceSettingsStore;

    private World? _selectedWorld;
    private DeviceSettings _deviceSettings = DeviceSettingsStore.CreateInitial(hasManagedWorlds: false);
    private bool _deviceSettingsUsableForRemote;
    private bool _foregroundBusy;
    private bool _lifecycleBusy;
    private bool _isBusy;

    public MainWindow()
    {
        InitializeComponent();
        InitializeLiveRegionAnnouncements();
        InitializeWorldLobbyUi();
        InitializeNativeWorldCreationUi();
        InitializeGameTechnicalReadinessUi();

        // App resolves/migrates the one durable local root while it owns the desktop single-instance
        // boundary, before this window can construct any storage/runtime writer. From here on every
        // SafeWorld-owned location comes from one explicit storage layout.
        var storageLayout = DesktopStorageLayout.FromResolvedRoot();
        _storageRoot = storageLayout.WorldDataRoot;
        var localStorage = new LocalWorldStorage(_storageRoot);
        _storage = new OwnedWorldLocationObservedWorldStorage(
            localStorage,
            RequestOwnedWorldLocationPublication);
        _workspaceRecoveryStore = new LocalWorkspaceRecoveryStore(_storageRoot);
        _localSessionCoordinator = new LocalWorldSessionCoordinator();
        _localManagedSessionGate = new ManagedWritableSessionGate();
        _lifecycle = new WorldLifecycleService(
            _storage,
            _localSessionCoordinator,
            _workspaceRecoveryStore,
            _localManagedSessionGate,
            CreateDesktopLifecycleObserver(),
            new ManagedWorkspaceStorage(storageLayout.ManagedWorkspacesRoot));
        _deviceSettingsStore = new DeviceSettingsStore(storageLayout.DeviceSettingsPath);

        Closed += (_, _) =>
        {
            DisposeOwnedWorldLocationMigrationState();
            DisposeRemoteRuntime();
        };
        InitializeTray();
    }

    private void InitializeLiveRegionAnnouncements()
    {
        RegisterLiveRegion(StatusText);
        RegisterLiveRegion(EnvironmentReadinessText);
    }

    private static void RegisterLiveRegion(TextBlock textBlock)
    {
        var textDescriptor = DependencyPropertyDescriptor.FromProperty(
            TextBlock.TextProperty,
            typeof(TextBlock));
        textDescriptor?.AddValueChanged(
            textBlock,
            (_, _) => RaiseLiveRegionChanged(textBlock));
    }

    private static void RaiseLiveRegionChanged(TextBlock textBlock)
    {
        var peer = UIElementAutomationPeer.FromElement(textBlock) ??
                   UIElementAutomationPeer.CreatePeerForElement(textBlock);
        peer?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
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
                    "Safe World kept hosting and shared Worlds disabled on this device because its durable device settings could not be loaded.",
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
                "World added, but hosting preference was not saved",
                new InvalidOperationException(
                    "The World was added successfully. Enable 'Allow this device to host' manually if this device should host Worlds.",
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
            ShowError("Safe World operation failed", exception);
        }
        finally
        {
            SetBusy(false);
        }
    }

    /// <summary>
    /// Sets foreground-operation presentation ownership only. Lifecycle-critical capture/commit owns
    /// a separate busy reason, so an unrelated operation finishing cannot unlock the shell while the
    /// hosted World is being captured or committed.
    /// </summary>
    private void SetBusy(bool isBusy)
    {
        _foregroundBusy = isBusy;
        ApplyBusyState();
    }

    private void SetLifecycleBusy(bool isBusy)
    {
        _lifecycleBusy = isBusy;
        ApplyBusyState();
    }

    private void ApplyBusyState()
    {
        var isBusy = _foregroundBusy || _lifecycleBusy;
        _isBusy = isBusy;
        RefreshButton.IsEnabled = !isBusy;
        RefreshGameButton.IsEnabled = !isBusy;
        OpenImportButton.IsEnabled = !isBusy;
        GameLibraryList.IsEnabled = !isBusy;
        BackToGamesButton.IsEnabled = !isBusy;
        AllowHostingCheckBox.IsEnabled = !isBusy;
        WorldList.IsEnabled = !isBusy;
        SetOwnedPrivateWorldCatalogBusyState(isBusy);
        UpdateNativeWorldCreationActionState();
        UpdateUnifiedActionState();
        UpdateUnifiedImportActionState();
        UpdateWorldVersionPolicyUi();
        UpdateEnvironmentReadinessUi();
        UpdateResponsibilityPresentation();
        UpdatePortableWorldExportBusyState();
        UpdateWorldHistoryActionState();
    }

    private void UpdateHostingPreferenceText()
        => HostingPreferenceText.Text = _deviceSettings.AllowHosting
            ? DesktopText.HostingAllowedOnDevice
            : DesktopText.HostingDisabledOnDevice;

    private static string FormatSharingMode(WorldSharingMode sharingMode)
        => sharingMode == WorldSharingMode.LocalOnly
            ? DesktopText.OnlyOnThisPc
            : DesktopText.Shared;

    private static UserIdentity GetLocalUser()
        => new(
            Provider: "local",
            ExternalId: Environment.UserName,
            DisplayName: Environment.UserName);

    private static void ShowError(string title, Exception exception)
    {
        var incident = LocalDiagnosticLog.TryWriteException(
            exception,
            DesktopLocalDataRoot.GetDiagnosticsRoot());
        var diagnosticReference = incident.LogPath is null
            ? $"Incident ID: {incident.Id}"
            : $"Incident ID: {incident.Id}{Environment.NewLine}Diagnostic log: {incident.LogPath}";
        MessageBox.Show(
            $"{DesktopErrorMessage.Safe(exception)}{Environment.NewLine}{Environment.NewLine}{diagnosticReference}",
            title,
            MessageBoxButton.OK,
            MessageBoxImage.Error);
    }
}
