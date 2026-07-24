using System.IO;
using System.Windows;
using SharedWorlds.Core.Domain;
using SharedWorlds.Core.Worlds;

namespace SharedWorlds.Desktop;

public partial class MainWindow
{
    private async void RetryCleanupButton_Click(object sender, RoutedEventArgs e)
    {
        var world = _selectedWorld;
        if (world is null || !TryGetAdapter(world.GameAdapterId, out var adapter))
        {
            return;
        }

        if (world.SharingMode == WorldSharingMode.Shared && !HasAuthoritativeRuntimeForWorld(world))
        {
            StatusText.Text =
                "Reconnect authenticated Steward authority before resolving cleanup for this shared World.";
            return;
        }

        await RunOperationAsync(
            $"Retrying workspace cleanup for {world.Name}...",
            async () =>
            {
                try
                {
                    var record = (await _workspaceRecoveryStore.ListAsync())
                        .Where(candidate =>
                            candidate.WorldId == world.Id &&
                            candidate.Status == WorkspaceRecoveryStatus.CleanupPending)
                        .OrderBy(candidate => candidate.CreatedAt)
                        .ThenBy(candidate => candidate.Id.ToString(), StringComparer.Ordinal)
                        .FirstOrDefault()
                        ?? throw new InvalidOperationException(
                            "No cleanup-only workspace responsibility exists for this World.");

                    // A missing installation is valid only when adapter-owned workspace cleanup already
                    // succeeded and journal removal is the sole remaining work. Otherwise select an
                    // installation by the exact environment that created the preserved workspace.
                    GameInstallation? installation = null;
                    if (Directory.Exists(record.WorkingDirectory))
                    {
                        installation = await GetReadyInstallationForRecoveryRecordAsync(
                            world,
                            adapter,
                            record);
                    }

                    var cleanup = new WorkspaceCleanupRecoveryService(
                        GetStorageForWorld(world),
                        _workspaceRecoveryStore);
                    await cleanup.RetryAsync(
                        world.Id,
                        adapter,
                        installation);
                    StatusText.Text = $"Cleanup responsibility for '{world.Name}' is resolved.";
                }
                finally
                {
                    await InitializeRuntimeResponsibilityAsync();
                    RefreshRuntimePresentation();
                }
            });
    }
}
