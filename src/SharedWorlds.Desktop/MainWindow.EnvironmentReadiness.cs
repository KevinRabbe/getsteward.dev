using SharedWorlds.Core.Domain;
using SharedWorlds.Core.Environment;
using SharedWorlds.Core.Worlds;

namespace SharedWorlds.Desktop;

public partial class MainWindow
{
    private EnvironmentVerificationReport? _environmentVerification;

    private async void VerifyEnvironmentButton_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        var world = _selectedWorld;
        if (world is null || !TryGetAdapter(world.GameAdapterId, out var adapter))
        {
            return;
        }

        if (world.SharingMode == WorldSharingMode.Shared && !HasAuthoritativeRuntimeForWorld(world))
        {
            StatusText.Text =
                "Reconnect authenticated Steward authority before verifying a shared World's canonical environment.";
            return;
        }

        await RunOperationAsync(
            $"Verifying the exact environment for {world.Name}...",
            async () =>
            {
                var installation = await GetGameInstallationAsync(adapter);
                var service = new WorldEnvironmentService(GetStorageForWorld(world));
                _environmentVerification = await service.VerifyAsync(
                    world.Id,
                    adapter,
                    installation);

                UpdateEnvironmentReadinessUi();
                StatusText.Text = !_environmentVerification.IsReady
                    ? $"The exact environment for '{world.Name}' is not ready on this device."
                    : $"This device is ready to play '{world.Name}' with its exact environment.";
            });

        UpdateEnvironmentReadinessUi();
    }

    private async void RepairEnvironmentButton_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        var world = _selectedWorld;
        if (world is null ||
            _environmentVerification?.CanRepairAutomatically != true ||
            !TryGetAdapter(world.GameAdapterId, out var adapter))
        {
            return;
        }

        if (world.SharingMode == WorldSharingMode.Shared && !HasAuthoritativeRuntimeForWorld(world))
        {
            StatusText.Text =
                "Reconnect authenticated Steward authority before repairing against a shared World's canonical environment.";
            return;
        }

        await RunOperationAsync(
            $"Repairing the local environment for {world.Name}...",
            async () =>
            {
                var installation = await GetGameInstallationAsync(adapter);
                var service = new WorldEnvironmentService(GetStorageForWorld(world));
                var result = await service.RepairAsync(
                    world.Id,
                    adapter,
                    installation);

                _environmentVerification = result.Verification;
                UpdateEnvironmentReadinessUi();
                StatusText.Text = result.Message;
            });

        UpdateEnvironmentReadinessUi();
    }

    private bool IsSelectedWorldEnvironmentReadyForPlay()
    {
        var world = _selectedWorld;
        return world is not null &&
               HasAuthoritativeRuntimeForWorld(world) &&
               (world.SharingMode == WorldSharingMode.LocalOnly ||
                _environmentVerification?.IsReady == true);
    }

    private void ResetEnvironmentReadinessUi()
    {
        _environmentVerification = null;
        UpdateEnvironmentReadinessUi();
    }

    private void UpdateEnvironmentReadinessUi()
    {
        var world = _selectedWorld;
        if (world is null)
        {
            EnvironmentReadinessText.Text = string.Empty;
            VerifyEnvironmentButton.IsEnabled = false;
            RepairEnvironmentButton.IsEnabled = false;
            UpdateUnifiedActionState();
            return;
        }

        if (world.SharingMode == WorldSharingMode.Shared && !HasAuthoritativeRuntimeForWorld(world))
        {
            EnvironmentReadinessText.Text =
                "Authenticated Steward authority is not connected. Verification and repair are blocked so this device cannot act on stale local shared-World metadata.";
            VerifyEnvironmentButton.IsEnabled = false;
            VerifyEnvironmentButton.ToolTip = "Reconnect Steward to load the canonical shared environment first.";
            RepairEnvironmentButton.IsEnabled = false;
            RepairEnvironmentButton.ToolTip = "Reconnect Steward before repair.";
            UpdateUnifiedActionState();
            return;
        }

        VerifyEnvironmentButton.IsEnabled = !_isBusy;
        VerifyEnvironmentButton.ToolTip = "Verify this device against the World's canonical environment.";

        if (_environmentVerification is null)
        {
            EnvironmentReadinessText.Text =
                "Not checked yet. Verify tests the same exact environment reproduction path used before play without launching the game or changing the World.";
            RepairEnvironmentButton.IsEnabled = false;
            RepairEnvironmentButton.ToolTip = "Run Verify first.";
            UpdateUnifiedActionState();
            return;
        }

        if (_environmentVerification.IsReady)
        {
            EnvironmentReadinessText.Text =
                "Ready. This device can reproduce the World's exact game version, mods and recorded environment requirements.";
            RepairEnvironmentButton.IsEnabled = false;
            RepairEnvironmentButton.ToolTip = "No repair is needed.";
            UpdateUnifiedActionState();
            return;
        }

        var issueText = string.Join(
            Environment.NewLine,
            _environmentVerification.Issues.Select(issue => $"• {issue.Message}"));
        EnvironmentReadinessText.Text = issueText;
        RepairEnvironmentButton.IsEnabled = !_isBusy && _environmentVerification.CanRepairAutomatically;
        RepairEnvironmentButton.ToolTip = _environmentVerification.CanRepairAutomatically
            ? "Apply only adapter-defined safe local repairs, then verify again."
            : "SharedWorlds does not have a safe automatic repair for this problem yet.";
        UpdateUnifiedActionState();
    }
}
