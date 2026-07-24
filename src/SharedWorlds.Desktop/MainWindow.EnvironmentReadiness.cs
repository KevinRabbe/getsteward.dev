using System.Windows.Automation;
using SharedWorlds.Core.Domain;
using SharedWorlds.Core.Environment;
using SharedWorlds.Core.Worlds;

namespace SharedWorlds.Desktop;

public partial class MainWindow
{
    private EnvironmentVerificationReport? _environmentVerification;
    private WorldId? _environmentVerificationWorldId;
    private RevisionId? _environmentVerificationRevisionId;

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
                var verification = await service.VerifyAsync(
                    world.Id,
                    adapter,
                    installation);
                RememberEnvironmentVerification(world, verification);

                UpdateEnvironmentReadinessUi();
                StatusText.Text = !verification.IsReady
                    ? $"The exact environment for '{world.Name}' is not ready on this device."
                    : $"This device is ready to play '{world.Name}' with its exact environment.";
            });

        UpdateEnvironmentReadinessUi();
    }

    private async void RepairEnvironmentButton_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        var world = _selectedWorld;
        var currentVerification = GetEnvironmentVerificationFor(world);
        if (world is null ||
            currentVerification?.CanRepairAutomatically != true ||
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

                RememberEnvironmentVerification(world, result.Verification);
                UpdateEnvironmentReadinessUi();
                StatusText.Text = result.Message;
            });

        UpdateEnvironmentReadinessUi();
    }

    private bool IsSelectedWorldEnvironmentReadyForPlay()
    {
        var world = _selectedWorld;
        var verification = GetEnvironmentVerificationFor(world);
        return world is not null &&
               HasAuthoritativeRuntimeForWorld(world) &&
               (world.SharingMode == WorldSharingMode.LocalOnly ||
                verification?.IsReady == true);
    }

    private EnvironmentVerificationReport? GetEnvironmentVerificationFor(World? world)
    {
        if (world is null ||
            _environmentVerification is null ||
            _environmentVerificationWorldId != world.Id ||
            _environmentVerificationRevisionId != world.CurrentEnvironmentRevisionId)
        {
            return null;
        }

        return _environmentVerification;
    }

    private void RememberEnvironmentVerification(
        World world,
        EnvironmentVerificationReport verification)
    {
        _environmentVerification = verification;
        _environmentVerificationWorldId = world.Id;
        _environmentVerificationRevisionId = world.CurrentEnvironmentRevisionId;
    }

    private void ResetEnvironmentReadinessUi()
    {
        _environmentVerification = null;
        _environmentVerificationWorldId = null;
        _environmentVerificationRevisionId = null;
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
            SetEnvironmentActionHelp("Select a World.", "Select a World.");
            UpdateUnifiedActionState();
            return;
        }

        if (world.SharingMode == WorldSharingMode.Shared && !HasAuthoritativeRuntimeForWorld(world))
        {
            EnvironmentReadinessText.Text =
                "Authenticated Steward authority is not connected. Verification and repair are blocked so this device cannot act on stale local shared-World metadata.";
            VerifyEnvironmentButton.IsEnabled = false;
            RepairEnvironmentButton.IsEnabled = false;
            SetEnvironmentActionHelp(
                "Reconnect Steward to load the canonical shared environment first.",
                "Reconnect Steward before repair.");
            UpdateUnifiedActionState();
            return;
        }

        VerifyEnvironmentButton.IsEnabled = !_isBusy;
        const string verifyHelp = "Verify this device against the World's canonical environment.";

        var verification = GetEnvironmentVerificationFor(world);
        if (verification is null)
        {
            EnvironmentReadinessText.Text =
                "Not checked yet. Verify tests the same exact environment reproduction path used before play without launching the game or changing the World.";
            RepairEnvironmentButton.IsEnabled = false;
            SetEnvironmentActionHelp(verifyHelp, "Run Verify first.");
            UpdateUnifiedActionState();
            return;
        }

        if (verification.IsReady)
        {
            EnvironmentReadinessText.Text =
                "Ready. This device can reproduce the World's exact game version, mods and recorded environment requirements.";
            RepairEnvironmentButton.IsEnabled = false;
            SetEnvironmentActionHelp(verifyHelp, "No repair is needed.");
            UpdateUnifiedActionState();
            return;
        }

        var issueText = string.Join(
            Environment.NewLine,
            verification.Issues.Select(issue => $"• {issue.Message}"));
        EnvironmentReadinessText.Text = issueText;
        RepairEnvironmentButton.IsEnabled = !_isBusy && verification.CanRepairAutomatically;
        SetEnvironmentActionHelp(
            verifyHelp,
            verification.CanRepairAutomatically
                ? "Apply only adapter-defined safe local repairs, then verify again."
                : "Steward does not have a safe automatic repair for this problem yet.");
        UpdateUnifiedActionState();
    }

    private void SetEnvironmentActionHelp(string verifyHelp, string repairHelp)
    {
        VerifyEnvironmentButton.ToolTip = verifyHelp;
        RepairEnvironmentButton.ToolTip = repairHelp;
        AutomationProperties.SetHelpText(VerifyEnvironmentButton, verifyHelp);
        AutomationProperties.SetHelpText(RepairEnvironmentButton, repairHelp);
    }
}
