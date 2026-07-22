using System.Windows;
using SharedWorlds.Core.Domain;

namespace SharedWorlds.Desktop;

public partial class MainWindow
{
    private async void RetryPendingSyncButton_Click(object sender, RoutedEventArgs e)
    {
        var world = _selectedWorld;
        var remote = _remoteRuntime;
        if (world is null ||
            remote is null ||
            !_remoteWorldIds.Contains(world.Id) ||
            !TryGetAdapter(world.GameAdapterId, out var adapter))
        {
            StatusText.Text =
                "Pending sync can be retried only after this shared World is authenticated with Steward.";
            return;
        }

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
                "This recovery is not a pending-sync retry. Steward left its evidence untouched for the appropriate recovery path.";
            return;
        }

        await RunOperationAsync(
            $"Reconciling pending sync for {world.Name}...",
            async () =>
            {
                try
                {
                    var installation = await GetGameInstallationAsync(adapter);
                    var updated = await remote.PendingSyncRecovery.RetryAsync(
                        world.Id,
                        adapter,
                        installation,
                        remote.User);

                    _selectedWorld = updated;
                    await RefreshUnifiedWorldsAsync(updated.Id, preserveStatus: true);
                    StatusText.Text =
                        $"Pending sync for '{updated.Name}' is resolved at canonical revision {updated.CurrentStateRevisionId}.";
                }
                finally
                {
                    // Recovery service changes the durable recovery journal directly rather than
                    // emitting normal WorldLifecycleService phases. Re-read that journal so tray,
                    // quit guard, banners and writable-action guards cannot remain stale.
                    await InitializeRuntimeResponsibilityAsync();
                    RefreshRuntimePresentation();
                }
            });
    }
}
