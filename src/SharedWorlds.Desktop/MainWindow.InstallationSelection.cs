using System.Windows;
using SharedWorlds.Core.Abstractions;
using SharedWorlds.Core.Domain;
using SharedWorlds.Core.Environment;
using SharedWorlds.Core.Worlds;

namespace SharedWorlds.Desktop;

public partial class MainWindow
{
    private GameInstallation? _selectedVerifiedInstallation;
    private WorldId? _selectedVerifiedInstallationWorldId;
    private RevisionId? _selectedVerifiedInstallationEnvironmentRevisionId;

    private void InitializeInstallationAwareWorldActions()
    {
        ContinueButton.Click -= UnifiedContinueButton_Click;
        HostButton.Click -= UnifiedHostButton_Click;
        VerifyEnvironmentButton.Click -= VerifyEnvironmentButton_Click;
        RepairEnvironmentButton.Click -= RepairEnvironmentButton_Click;

        ContinueButton.Click += InstallationAwareContinueButton_Click;
        HostButton.Click += InstallationAwareHostButton_Click;
        VerifyEnvironmentButton.Click += InstallationAwareVerifyEnvironmentButton_Click;
        RepairEnvironmentButton.Click += InstallationAwareRepairEnvironmentButton_Click;
    }

    private async void InstallationAwareVerifyEnvironmentButton_Click(
        object sender,
        RoutedEventArgs e)
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
                var selection = await SelectInstallationForWorldAsync(world, adapter);
                RememberEnvironmentVerification(world, selection.Verification);
                RememberVerifiedInstallation(world, selection.Installation);
                UpdateEnvironmentReadinessUi();
                StatusText.Text = !selection.Verification.IsReady
                    ? $"The exact environment for '{world.Name}' is not ready on any discovered {adapter.DisplayName} installation."
                    : $"This device is ready to play '{world.Name}' with its exact environment.";
            });

        UpdateEnvironmentReadinessUi();
    }

    private async void InstallationAwareRepairEnvironmentButton_Click(
        object sender,
        RoutedEventArgs e)
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
                var installation = GetRememberedVerifiedInstallation(world);
                if (installation is null)
                {
                    var selection = await SelectInstallationForWorldAsync(world, adapter);
                    installation = selection.Installation;
                    RememberEnvironmentVerification(world, selection.Verification);
                    RememberVerifiedInstallation(world, installation);
                }

                var service = new WorldEnvironmentService(GetStorageForWorld(world));
                var result = await service.RepairAsync(
                    world.Id,
                    adapter,
                    installation);

                RememberEnvironmentVerification(world, result.Verification);
                RememberVerifiedInstallation(world, installation);
                UpdateEnvironmentReadinessUi();
                StatusText.Text = result.Message;
            });

        UpdateEnvironmentReadinessUi();
    }

    private async void InstallationAwareContinueButton_Click(
        object sender,
        RoutedEventArgs e)
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
                var installation = await GetReadyInstallationForWorldAsync(world, adapter);
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

    private async void InstallationAwareHostButton_Click(
        object sender,
        RoutedEventArgs e)
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
                var installation = await GetReadyInstallationForWorldAsync(world, adapter);
                StatusText.Text =
                    $"{adapter.DisplayName} is running. End the game/server session normally; Steward will then capture and commit the new canonical revision.";

                var lifecycle = GetLifecycleForWorld(world);
                var managedHostAdapter = GetManagedHostAdapterForWorld(world, adapter);
                var updated = await lifecycle.ContinueAsHostAsync(
                    world.Id,
                    managedHostAdapter,
                    installation,
                    GetUserForWorld(world));

                StatusText.Text =
                    $"Hosted World '{updated.Name}' committed as revision {updated.CurrentStateRevisionId}.";
                await RefreshUnifiedWorldsAsync(updated.Id, preserveStatus: true);
            });
    }

    private async Task<GameInstallation> GetReadyInstallationForWorldAsync(
        World world,
        IGameAdapter adapter)
    {
        var remembered = GetRememberedVerifiedInstallation(world);
        if (remembered is not null && GetEnvironmentVerificationFor(world)?.IsReady == true)
        {
            return remembered;
        }

        var selection = await SelectInstallationForWorldAsync(world, adapter);
        RememberEnvironmentVerification(world, selection.Verification);
        RememberVerifiedInstallation(world, selection.Installation);
        UpdateEnvironmentReadinessUi();
        if (!selection.Verification.IsReady)
        {
            var reason = selection.Verification.Issues.Count == 0
                ? "No discovered installation can reproduce the canonical environment."
                : string.Join("; ", selection.Verification.Issues.Select(issue => issue.Message));
            throw new InvalidOperationException(
                $"No discovered {adapter.DisplayName} installation is ready for World '{world.Name}': {reason}");
        }

        return selection.Installation;
    }

    private async Task<GameInstallation> GetReadyInstallationForRecoveryRecordAsync(
        World world,
        IGameAdapter adapter,
        WorkspaceRecoveryRecord record)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(adapter);
        ArgumentNullException.ThrowIfNull(record);

        if (record.WorldId != world.Id)
        {
            throw new InvalidOperationException(
                "The selected recovery record belongs to a different World.");
        }

        if (!string.Equals(record.AdapterId, adapter.Id, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The selected recovery record belongs to a different game adapter.");
        }

        var environmentRevisionId = record.EnvironmentRevisionId
            ?? throw new InvalidOperationException(
                "This recovery record does not identify the exact environment that created its workspace. Steward will preserve the workspace rather than guess.");
        var environment = await GetStorageForWorld(world).LoadEnvironmentRevisionAsync(
            world.Id,
            environmentRevisionId)
            ?? throw new InvalidOperationException(
                "The exact journaled environment required for recovery is unavailable.");
        if (!string.Equals(environment.Manifest.AdapterId, adapter.Id, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The journaled recovery environment belongs to a different game adapter.");
        }

        var installations = (await adapter.DiscoverInstallationsAsync())
            .OrderBy(installation => installation.RootPath, StringComparer.OrdinalIgnoreCase)
            .ThenBy(installation => installation.Id, StringComparer.Ordinal)
            .ToArray();
        if (installations.Length == 0)
        {
            throw new InvalidOperationException(
                $"{adapter.DisplayName} installation not found on this device.");
        }

        var rejected = new List<InstallationSelection>(installations.Length);
        foreach (var installation in installations)
        {
            var verification = await adapter.VerifyEnvironmentAsync(
                installation,
                environment.Manifest);
            if (verification.IsReady)
            {
                return installation;
            }

            rejected.Add(new InstallationSelection(installation, verification));
        }

        var best = rejected
            .OrderByDescending(selection => selection.Verification.CanRepairAutomatically)
            .ThenBy(selection => selection.Verification.Issues.Count)
            .ThenBy(selection => selection.Installation.RootPath, StringComparer.OrdinalIgnoreCase)
            .First();
        var reason = best.Verification.Issues.Count == 0
            ? "No discovered installation can reproduce the journaled environment."
            : string.Join("; ", best.Verification.Issues.Select(issue => issue.Message));
        throw new InvalidOperationException(
            $"No discovered {adapter.DisplayName} installation can reproduce recovery environment {environmentRevisionId}: {reason}");
    }

    private async Task<InstallationSelection> SelectInstallationForWorldAsync(
        World world,
        IGameAdapter adapter)
    {
        var installations = (await adapter.DiscoverInstallationsAsync())
            .OrderBy(installation => installation.RootPath, StringComparer.OrdinalIgnoreCase)
            .ThenBy(installation => installation.Id, StringComparer.Ordinal)
            .ToArray();
        if (installations.Length == 0)
        {
            throw new InvalidOperationException(
                $"{adapter.DisplayName} installation not found on this device.");
        }

        var service = new WorldEnvironmentService(GetStorageForWorld(world));
        var evaluated = new List<InstallationSelection>(installations.Length);
        foreach (var installation in installations)
        {
            var verification = await service.VerifyAsync(
                world.Id,
                adapter,
                installation);
            var selection = new InstallationSelection(installation, verification);
            if (verification.IsReady)
            {
                return selection;
            }

            evaluated.Add(selection);
        }

        return evaluated
            .OrderByDescending(selection => selection.Verification.CanRepairAutomatically)
            .ThenBy(selection => selection.Verification.Issues.Count)
            .ThenBy(selection => selection.Installation.RootPath, StringComparer.OrdinalIgnoreCase)
            .First();
    }

    private void RememberVerifiedInstallation(
        World world,
        GameInstallation installation)
    {
        _selectedVerifiedInstallation = installation;
        _selectedVerifiedInstallationWorldId = world.Id;
        _selectedVerifiedInstallationEnvironmentRevisionId = world.CurrentEnvironmentRevisionId;
    }

    private GameInstallation? GetRememberedVerifiedInstallation(World world)
    {
        if (_selectedVerifiedInstallation is null ||
            _selectedVerifiedInstallationWorldId != world.Id ||
            _selectedVerifiedInstallationEnvironmentRevisionId != world.CurrentEnvironmentRevisionId)
        {
            return null;
        }

        return _selectedVerifiedInstallation;
    }

    private sealed record InstallationSelection(
        GameInstallation Installation,
        EnvironmentVerificationReport Verification);
}
