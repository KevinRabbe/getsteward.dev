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
            "Gameplay changes that were never committed will be permanently abandoned. " +
            "The last committed World revision remains unchanged.\n\n" +
            "If this is an older workspace without exact environment evidence, Safe World will preserve " +
            "its files as abandoned evidence instead of guessing how to delete them.",
            "Continue from last safe state?",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);
        if (confirmation != MessageBoxResult.Yes)
        {
            return;
        }

        await RunOperationAsync(
            $"Resolving interrupted session for {world.Name}...",
            async () =>
            {
                try
                {
                    _ = await GetInterruptedWorkspaceRecordAsync(world.Id);

                    var decision = new InterruptedWorkspaceRecoveryDecisionService(
                        _workspaceRecoveryStore);
                    var discard = await decision.PrepareDiscardAsync(world.Id, adapter.Id);

                    if (discard.Status == WorkspaceRecoveryStatus.CleanupPending)
                    {
                        var cleanup = new WorkspaceCleanupRecoveryService(
                            GetStorageForWorld(world),
                            _workspaceRecoveryStore);
                        await cleanup.RetryAsync(
                            world.Id,
                            adapter,
                            installation: null);
                        StatusText.Text =
                            $"'{world.Name}' is back on its last safe state. Its interrupted workspace was removed.";
                    }
                    else if (discard.Status == WorkspaceRecoveryStatus.Abandoned)
                    {
                        StatusText.Text =
                            $"'{world.Name}' is back on its last safe state. Its legacy workspace was preserved as abandoned evidence and no longer blocks other Worlds.";
                    }
                    else
                    {
                        throw new InvalidOperationException(
                            $"Unexpected interrupted-discard state '{discard.Status}'.");
                    }
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
