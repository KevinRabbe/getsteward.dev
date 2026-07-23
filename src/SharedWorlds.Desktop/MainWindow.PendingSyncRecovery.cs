using System.Windows;
using SharedWorlds.Core.Abstractions;
using SharedWorlds.Core.Domain;
using SharedWorlds.Infrastructure.Recovery;

namespace SharedWorlds.Desktop;

public partial class MainWindow
{
    private async void RetryPendingSyncButton_Click(object sender, RoutedEventArgs e)
    {
        var world = _selectedWorld;
        if (world is null || !TryGetAdapter(world.GameAdapterId, out var adapter))
        {
            return;
        }

        if (world.SharingMode == WorldSharingMode.Shared && !HasAuthoritativeRuntimeForWorld(world))
        {
            StatusText.Text =
                "Reconnect authenticated Steward authority before retrying recovery for this shared World.";
            return;
        }

        await RunOperationAsync(
            $"Reconciling recovery for {world.Name}...",
            async () =>
            {
                try
                {
                    var updated = await RetryPendingRecoveryCoreAsync(world, adapter);
                    _selectedWorld = updated;
                    await RefreshUnifiedWorldsAsync(updated.Id, preserveStatus: true);
                    StatusText.Text =
                        $"Recovery for '{updated.Name}' is resolved at canonical revision {updated.CurrentStateRevisionId}.";
                }
                finally
                {
                    await RefreshResponsibilityAfterRecoveryAsync();
                }
            });
    }

    private async Task<World> RetryPendingRecoveryCoreAsync(
        World world,
        IGameAdapter adapter,
        GameInstallation? knownInstallation = null)
    {
        var records = await _workspaceRecoveryStore.ListAsync();
        if (!records.Any(record =>
                record.WorldId == world.Id &&
                record.Status == WorkspaceRecoveryStatus.RecoveryPending))
        {
            throw new InvalidOperationException(
                "This responsibility is not a pending recovery. Steward left its evidence untouched for the appropriate recovery path.");
        }

        var installation = knownInstallation ?? await GetGameInstallationAsync(adapter);
        if (_remoteWorldIds.Contains(world.Id))
        {
            var remote = _remoteRuntime
                ?? throw new InvalidOperationException(
                    "The authenticated Steward runtime disappeared before shared recovery could start.");
            return await remote.PendingSyncRecovery.RetryAsync(
                world.Id,
                adapter,
                installation,
                remote.User);
        }

        if (world.SharingMode == WorldSharingMode.Shared)
        {
            throw new InvalidOperationException(
                "A shared World can never use local recovery authority. Reconnect Steward first.");
        }

        var localRecovery = new LocalPendingWorkspaceRecoveryService(
            _storage,
            _localSessionCoordinator,
            _workspaceRecoveryStore,
            _localManagedSessionGate);
        return await localRecovery.RetryAsync(
            world.Id,
            adapter,
            installation,
            GetLocalUser());
    }

    private async Task RefreshResponsibilityAfterRecoveryAsync()
    {
        // Recovery services change the durable journal directly rather than emitting the normal
        // lifecycle phases. Re-read it so tray, quit guard, banners and writable-action guards cannot
        // remain stale after success or a status transition.
        await InitializeRuntimeResponsibilityAsync();
        RefreshRuntimePresentation();
    }
}
