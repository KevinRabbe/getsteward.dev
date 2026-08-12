using System.Windows.Automation;
using SharedWorlds.Core.Domain;
using SharedWorlds.Core.Environment;
using SharedWorlds.Core.Worlds;

namespace SharedWorlds.Desktop;

public partial class MainWindow
{
    private readonly Dictionary<EnvironmentVerificationKey, EnvironmentVerificationReport> _environmentVerifications = [];

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
                "Reconnect Safe World before verifying this shared World.";
            return;
        }

        await RunOperationAsync(
            $"Verifying {world.Name}...",
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
                    ? $"'{world.Name}' is not ready on this PC."
                    : $"'{world.Name}' is ready on this PC.";
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
                "Reconnect Safe World before repairing this shared World.";
            return;
        }

        await RunOperationAsync(
            $"Repairing {world.Name}...",
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
        if (world is null)
        {
            return null;
        }

        return _environmentVerifications.TryGetValue(
            GetEnvironmentVerificationKey(world),
            out var verification)
            ? verification
            : null;
    }

    private void RememberEnvironmentVerification(
        World world,
        EnvironmentVerificationReport verification)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(verification);
        _environmentVerifications[GetEnvironmentVerificationKey(world)] = verification;
    }

    private static EnvironmentVerificationKey GetEnvironmentVerificationKey(World world)
    {
        ArgumentNullException.ThrowIfNull(world);
        return new EnvironmentVerificationKey(
            world.Id,
            world.CurrentEnvironmentRevisionId);
    }

    private void ResetEnvironmentReadinessUi()
    {
        // Navigation and ordinary presentation refreshes must not discard a successful verification
        // for the same exact canonical environment revision. The revision-aware key itself makes a
        // changed environment fail closed without treating UI movement as an environment mutation.
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
            EnvironmentReadinessText.Text = "Reconnect Safe World to verify this World.";
            VerifyEnvironmentButton.IsEnabled = false;
            RepairEnvironmentButton.IsEnabled = false;
            SetEnvironmentActionHelp(
                "Reconnect Safe World first.",
                "Reconnect Safe World first.");
            UpdateUnifiedActionState();
            return;
        }

        VerifyEnvironmentButton.IsEnabled = !_isBusy;
        const string verifyHelp = "Check this PC against the World's exact game environment.";

        var verification = GetEnvironmentVerificationFor(world);
        if (verification is null)
        {
            EnvironmentReadinessText.Text = DesktopText.NotCheckedYet;
            RepairEnvironmentButton.IsEnabled = false;
            SetEnvironmentActionHelp(verifyHelp, "Run Verify first.");
            UpdateUnifiedActionState();
            return;
        }

        if (verification.IsReady)
        {
            EnvironmentReadinessText.Text = "Ready on this PC.";
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
                ? "Apply the safe repair and verify again."
                : "Safe World does not have an automatic repair for this problem yet.");
        UpdateUnifiedActionState();
    }

    private void SetEnvironmentActionHelp(string verifyHelp, string repairHelp)
    {
        VerifyEnvironmentButton.ToolTip = verifyHelp;
        RepairEnvironmentButton.ToolTip = repairHelp;
        AutomationProperties.SetHelpText(VerifyEnvironmentButton, verifyHelp);
        AutomationProperties.SetHelpText(RepairEnvironmentButton, repairHelp);
    }

    private readonly record struct EnvironmentVerificationKey(
        WorldId WorldId,
        RevisionId? EnvironmentRevisionId);
}
