using System.IO;
using System.Windows;
using System.Windows.Controls;
using SharedWorlds.Core.Abstractions;
using SharedWorlds.Core.Domain;
using SharedWorlds.GameAdapters.Palworld;

namespace SharedWorlds.Desktop;

public partial class MainWindow
{
    private readonly PalworldAdapter _palworldAdapter = new();

    private Button? _importPalworldButton;
    private Button? _hostPalworldButton;
    private bool _palworldUiInitialized;

    internal void InitializePalworldUi()
    {
        if (_palworldUiInitialized)
        {
            return;
        }

        _palworldUiInitialized = true;

        _importPalworldButton = new Button
        {
            Content = "Import newest Palworld",
            Margin = new Thickness(0, 0, 8, 8),
            ToolTip = "Import the newest local Palworld save as a managed private World."
        };
        _importPalworldButton.Click += ImportPalworldButton_Click;

        if (ImportSelectedButton.Parent is Panel importActions)
        {
            var index = importActions.Children.IndexOf(ImportSelectedButton);
            importActions.Children.Insert(index < 0 ? 0 : index + 1, _importPalworldButton);
        }

        _hostPalworldButton = new Button
        {
            Content = "Host Palworld",
            Margin = new Thickness(0, 0, 10, 10),
            Visibility = Visibility.Collapsed,
            ToolTip = "Restore the canonical revision, launch PalServer, then capture a new revision when the server closes."
        };
        _hostPalworldButton.Click += HostPalworldButton_Click;

        if (HostButton.Parent is Panel playActions)
        {
            var index = playActions.Children.IndexOf(HostButton);
            playActions.Children.Insert(index < 0 ? playActions.Children.Count : index + 1, _hostPalworldButton);
        }

        WorldList.SelectionChanged += (_, _) =>
        {
            UpdatePalworldPresentation();
            UpdatePalworldActionState();
        };
        WorldList.IsEnabledChanged += (_, _) => UpdatePalworldActionState();
        AllowHostingCheckBox.Click += (_, _) => UpdatePalworldActionState();

        UpdatePalworldPresentation();
        UpdatePalworldActionState();
    }

    private async void ImportPalworldButton_Click(object sender, RoutedEventArgs e)
    {
        await RunPalworldOperationAsync(
            "Looking for local Palworld Worlds...",
            async () =>
            {
                var installation = await GetPalworldInstallationAsync(requireDedicatedServer: false);
                var worlds = await _palworldAdapter.DiscoverWorldsAsync(installation);
                var localWorld = worlds
                    .Where(world => world.Id.StartsWith("local:", StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(world => GetLevelSaveLastWriteTimeUtc(world.SourcePath))
                    .FirstOrDefault()
                    ?? throw new InvalidOperationException(
                        "Palworld is installed, but no local World with Level.sav was detected.");

                var worldName = await CreateUniqueWorldNameAsync(localWorld.DisplayName);
                var imported = await _lifecycle.ImportAsync(
                    _palworldAdapter,
                    installation,
                    localWorld,
                    worldName,
                    GetLocalUser());

                await TryEnableCreatorDeviceHostingAsync();
                ImportPanel.Visibility = Visibility.Collapsed;
                StatusText.Text =
                    $"Imported '{imported.Name}'. Use Share World, then Host Palworld to play through Steward.";
                await RefreshWorldsAsync(imported.Id, preserveStatus: true);
            });
    }

    private async void HostPalworldButton_Click(object sender, RoutedEventArgs e)
    {
        var world = _selectedWorld;
        if (world is null ||
            !string.Equals(world.GameAdapterId, _palworldAdapter.Id, StringComparison.Ordinal))
        {
            StatusText.Text = "Select a managed Palworld World first.";
            return;
        }

        if (AllowHostingCheckBox.IsChecked != true)
        {
            StatusText.Text = "Enable 'Allow this device to host' in Device settings first.";
            return;
        }

        if (world.SharingMode != WorldSharingMode.Shared)
        {
            StatusText.Text = "Use Share World before starting a Palworld host session.";
            return;
        }

        await RunPalworldOperationAsync(
            $"Preparing canonical state for '{world.Name}'...",
            async () =>
            {
                var installation = await GetPalworldInstallationAsync(requireDedicatedServer: true);
                StatusText.Text =
                    "PalServer is running. Close its console to end the session; Steward will then capture and commit the new canonical revision.";

                var updated = await _lifecycle.ContinueAsHostAsync(
                    world.Id,
                    _palworldAdapter,
                    installation,
                    GetLocalUser());

                StatusText.Text =
                    $"Palworld session ended. '{updated.Name}' is now revision {updated.CurrentStateRevisionId}.";
                await RefreshWorldsAsync(updated.Id, preserveStatus: true);
            });
    }

    private async Task RunPalworldOperationAsync(string status, Func<Task> operation)
    {
        SetPalworldButtonsEnabled(false);
        try
        {
            await RunOperationAsync(status, operation);
        }
        finally
        {
            UpdatePalworldPresentation();
            UpdatePalworldActionState();
        }
    }

    private async Task<GameInstallation> GetPalworldInstallationAsync(bool requireDedicatedServer)
    {
        var installations = await _palworldAdapter.DiscoverInstallationsAsync();
        var installation = requireDedicatedServer
            ? installations.FirstOrDefault(HasInstalledPalworldDedicatedServer)
            : installations.FirstOrDefault();

        if (installation is not null)
        {
            return installation;
        }

        throw new InvalidOperationException(requireDedicatedServer
            ? "Palworld Dedicated Server is not installed or could not be detected on this device."
            : "Palworld is not installed or could not be detected on this device.");
    }

    private static bool HasInstalledPalworldDedicatedServer(GameInstallation installation)
    {
        return installation.Metadata is not null &&
               installation.Metadata.TryGetValue("dedicatedServerInstallState", out var installState) &&
               string.Equals(installState, "installed", StringComparison.OrdinalIgnoreCase) &&
               installation.Metadata.TryGetValue("dedicatedServerExecutablePath", out var executablePath) &&
               !string.IsNullOrWhiteSpace(executablePath) &&
               File.Exists(executablePath);
    }

    private void UpdatePalworldPresentation()
    {
        var isPalworld = _selectedWorld is not null &&
                         string.Equals(
                             _selectedWorld.GameAdapterId,
                             _palworldAdapter.Id,
                             StringComparison.Ordinal);

        ContinueButton.Visibility = isPalworld ? Visibility.Collapsed : Visibility.Visible;
        HostButton.Visibility = isPalworld ? Visibility.Collapsed : Visibility.Visible;

        if (_hostPalworldButton is not null)
        {
            _hostPalworldButton.Visibility = isPalworld
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        if (isPalworld)
        {
            GameText.Text = "Palworld";
        }
    }

    private void UpdatePalworldActionState()
    {
        if (_importPalworldButton is not null)
        {
            _importPalworldButton.IsEnabled = !_isBusy;
        }

        if (_hostPalworldButton is null)
        {
            return;
        }

        var world = _selectedWorld;
        var isPalworld = world is not null &&
                         string.Equals(world.GameAdapterId, _palworldAdapter.Id, StringComparison.Ordinal);
        var hostingAllowed = AllowHostingCheckBox.IsChecked == true;
        var isShared = world?.SharingMode == WorldSharingMode.Shared;

        _hostPalworldButton.IsEnabled = !_isBusy && isPalworld && hostingAllowed && isShared;
        _hostPalworldButton.ToolTip = !isPalworld
            ? "Select a managed Palworld World."
            : !hostingAllowed
                ? "Enable 'Allow this device to host' in Device settings first."
                : !isShared
                    ? "Use Share World before hosting."
                    : "Restore the canonical revision, launch PalServer, then capture a new revision when the server closes.";
    }

    private void SetPalworldButtonsEnabled(bool enabled)
    {
        if (_importPalworldButton is not null)
        {
            _importPalworldButton.IsEnabled = enabled;
        }

        if (_hostPalworldButton is not null)
        {
            _hostPalworldButton.IsEnabled = enabled;
        }
    }

    private static DateTime GetLevelSaveLastWriteTimeUtc(string worldPath)
    {
        try
        {
            var levelSavePath = Path.Combine(worldPath, "Level.sav");
            return File.Exists(levelSavePath)
                ? File.GetLastWriteTimeUtc(levelSavePath)
                : DateTime.MinValue;
        }
        catch (IOException)
        {
            return DateTime.MinValue;
        }
        catch (UnauthorizedAccessException)
        {
            return DateTime.MinValue;
        }
    }
}