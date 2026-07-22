using System.Windows;
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
                    var records = await _workspaceRecoveryStore.ListAsync();
                    var pending = records
                        .Where(record =>
                            record.WorldId == world.Id &&
                            record.Status == WorkspaceRecoveryStatus.RecoveryPending)
                        .OrderBy(record => record.CreatedAt)
                        .ThenBy(record => record.Id.ToString(), StringComparer.Ordinal)
                        .FirstOrDefault();
                    if (pending is null)
                    {
                        StatusText.Text =
                            "This responsibility is not a pending recovery. Steward left its evidence untouched for the appropriate recovery path.";
                        return;
                    }

                    var installation = await GetGameInstallationAsync(adapter);
                    World updated;
                    if (_remoteWorldIds.Contains(world.Id))
                    {
                        var remote = _remoteRuntime
                            ?? throw new InvalidOperationException(
                                "The authenticated Steward runtime disappeared before shared recovery could start.");
                        updated = await remote.PendingSyncRecovery.RetryAsync(
                            world.Id,
                            adapter,
                            installation,
                            remote.User);
                    }
                    else
                    {
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
                        updated = await localRecovery.RetryAsync(
                            world.Id,
                            adapter,
                            installation,
                            GetLocalUser());
                    }

                    _selectedWorld = updated;
                    await RefreshUnifiedWorldsAsync(updated.Id, preserveStatus: true);
                    StatusText.Text =
                        $"Recovery for '{updated.Name}' is resolved at canonical revision {updated.CurrentStateRevisionId}.";
                }
                finally
                {
                    // Recovery services change the durable journal directly rather than emitting the
                    // normal lifecycle phases. Re-read it so tray, quit guard, banners and writable
                    // action guards cannot remain stale after success or a status transition.
                    await InitializeRuntimeResponsibilityAsync();
                    RefreshRuntimePresentation();
                }
            });
    }
}
