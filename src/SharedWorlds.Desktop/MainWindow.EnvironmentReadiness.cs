using SharedWorlds.Core.Environment;
using SharedWorlds.Core.Worlds;

namespace SharedWorlds.Desktop;

public partial class MainWindow
{
    private EnvironmentVerificationReport? _environmentVerification;

    private async void VerifyEnvironmentButton_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        var world = _selectedWorld;
        if (world is null)
        {
            return;
        }

        await RunOperationAsync(
            $"Verifying the exact environment for {world.Name}...",
            async () =>
            {
                EnsureFactorioWorld(world);
                var installation = await GetFactorioInstallationAsync();
                var service = new WorldEnvironmentService(_storage);
                _environmentVerification = await service.VerifyAsync(
                    world.Id,
                    _factorioAdapter,
                    installation);

                UpdateEnvironmentReadinessUi();
                StatusText.Text = _environmentVerification.IsReady
                    ? $"This device is ready to play '{world.Name}' with its exact environment."
                    : $"The exact environment for '{world.Name}' is not ready on this device.";
            });
    }

    private async void RepairEnvironmentButton_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        var world = _selectedWorld;
        if (world is null || _environmentVerification?.CanRepairAutomatically != true)
        {
            return;
        }

        await RunOperationAsync(
            $"Repairing the local environment for {world.Name}...",
            async () =>
            {
                EnsureFactorioWorld(world);
                var installation = await GetFactorioInstallationAsync();
                var service = new WorldEnvironmentService(_storage);
                var result = await service.RepairAsync(
                    world.Id,
                    _factorioAdapter,
                    installation);

                _environmentVerification = result.Verification;
                UpdateEnvironmentReadinessUi();
                StatusText.Text = result.Message;
            });
    }

    private void ResetEnvironmentReadinessUi()
    {
        _environmentVerification = null;
        UpdateEnvironmentReadinessUi();
    }

    private void UpdateEnvironmentReadinessUi()
    {
        if (_selectedWorld is null)
        {
            EnvironmentReadinessText.Text = string.Empty;
            VerifyEnvironmentButton.IsEnabled = false;
            RepairEnvironmentButton.IsEnabled = false;
            return;
        }

        VerifyEnvironmentButton.IsEnabled = !_isBusy;

        if (_environmentVerification is null)
        {
            EnvironmentReadinessText.Text =
                "Not checked yet. Verify tests the same exact environment reproduction path used before play without launching the game or changing the World.";
            RepairEnvironmentButton.IsEnabled = false;
            RepairEnvironmentButton.ToolTip = "Run Verify first.";
            return;
        }

        if (_environmentVerification.IsReady)
        {
            EnvironmentReadinessText.Text =
                "Ready. This device can reproduce the World's exact game version, mods and recorded environment requirements.";
            RepairEnvironmentButton.IsEnabled = false;
            RepairEnvironmentButton.ToolTip = "No repair is needed.";
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
    }
}
