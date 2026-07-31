using System.IO;
using System.Windows;
using SharedWorlds.Core.Abstractions;
using SharedWorlds.Core.Domain;
using SharedWorlds.Core.Worlds;

namespace SharedWorlds.Desktop;

public partial class MainWindow
{
    private async void RecoverInterruptedSessionButton_Click(object sender, RoutedEventArgs e)
    {
        var world = _selectedWorld;
        if (world is null || !TryGetAdapter(world.GameAdapterId, out var adapter))
        {
            return;
        }

        if (world.SharingMode == WorldSharingMode.Shared && !HasAuthoritativeRuntimeForWorld(world))
        {
            StatusText.Text =
                "Reconnect Safe World before recovering changes into this interrupted shared World.";
            return;
        }

        await RunOperationAsync(
            $"Recovering interrupted session for {world.Name}...",
            async () =>
            {
                try
                {
                    var active = await GetInterruptedWorkspaceRecordAsync(world.Id);

                    // Verify a concrete installation against the exact environment that created the
                    // preserved workspace before changing Active -> RecoveryPending. Discovery order
                    // must never decide which installation is trusted for interrupted recovery.
                    var installation = await GetReadyInstallationForRecoveryRecordAsync(
                        world,
                        adapter,
                        active);
                    var decision = new InterruptedWorkspaceRecoveryDecisionService(
                        _workspaceRecoveryStore);
                    await decision.PrepareRecoveryAsync(world.Id, adapter.Id);

                    var updated = await RetryPendingRecoveryCoreAsync(
                        world,
                        adapter,
                        installation);
                    _selectedWorld = updated;
                    await RefreshUnifiedWorldsAsync(updated.Id, preserveStatus: true);
                    StatusText.Text =
                        $"Interrupted session for '{updated.Name}' was recovered as canonical revision {updated.CurrentStateRevisionId}.";
                }
                finally
                {
                    await RefreshResponsibilityAfterRecoveryAsync();
                }
            });
    }

    private async void DiscardInterruptedSessionButton_Click(object sender, RoutedEventArgs e)
    {
        var world = _selectedWorld;
        if (world is null || !TryGetAdapter(world.GameAdapterId, out var adapter))
        {
            return;
        }

        // Discard is intentionally local. It never writes a canonical World revision and therefore
        // must remain available when an incomplete/disconnected shared World has no backend authority.
        var confirmation = MessageBox.Show(
            this,
            $"Continue '{world.Name}' from its last safe state?\n\n" +
            "Safe World will remove the preserved interrupted workspace from this PC. " +
            "Gameplay changes that were never committed will be permanently discarded. " +
            "The last committed World revision remains unchanged.",
            "Continue from last safe state?",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);
        if (confirmation != MessageBoxResult.Yes)
        {
            return;
        }

        await RunOperationAsync(
            $"Removing interrupted workspace for {world.Name}...",
            async () =>
            {
                try
                {
                    var active = await GetInterruptedWorkspaceRecordAsync(world.Id);
                    GameInstallation? installation = null;
                    if (Directory.Exists(active.WorkingDirectory))
                    {
                        installation = await GetReadyInstallationForRecoveryRecordAsync(
                            world,
                            adapter,
                            active);
                    }

                    var decision = new InterruptedWorkspaceRecoveryDecisionService(
                        _workspaceRecoveryStore);
                    await decision.PrepareDiscardAsync(world.Id, adapter.Id);

                    var cleanup = new WorkspaceCleanupRecoveryService(
                        GetStorageForWorld(world),
                        _workspaceRecoveryStore);
                    await cleanup.RetryAsync(
                        world.Id,
                        adapter,
                        installation);
                    StatusText.Text =
                        $"'{world.Name}' is back on its last safe state. Uncommitted interrupted-session changes were discarded.";
                }
                finally
                {
                    await RefreshResponsibilityAfterRecoveryAsync();
                }
            });
    }

    private async Task<WorkspaceRecoveryRecord> GetInterruptedWorkspaceRecordAsync(WorldId worldId)
        => (await _workspaceRecoveryStore.ListAsync())
               .Where(record =>
                   record.WorldId == worldId &&
                   record.Status == WorkspaceRecoveryStatus.Active)
               .OrderBy(record => record.CreatedAt)
               .ThenBy(record => record.Id.ToString(), StringComparer.Ordinal)
               .FirstOrDefault()
           ?? throw new InvalidOperationException(
               "No interrupted Active workspace exists for this World.");
}
