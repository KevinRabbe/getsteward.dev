using System.Windows;
using SharedWorlds.Core.Abstractions;
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

                    var resolver = CreatePreparedWorldRecoveryResolver();
                    var pathWithoutInstallation = resolver.ResolveWorkingDirectoryWithoutInstallation(
                        record,
                        adapter.Id);

                    // Managed/legacy runtime that is already absent needs only journal removal. Native
                    // identity cannot be located without the current game installation, and present
                    // managed/legacy runtime also needs the adapter to finalize it safely.
                    GameInstallation? installation = null;
                    if (pathWithoutInstallation is null || Directory.Exists(pathWithoutInstallation))
                    {
                        installation = await GetReadyInstallationForRecoveryRecordAsync(
                            world,
                            adapter,
                            record);
                    }

                    var cleanup = new WorkspaceCleanupRecoveryService(
                        GetStorageForWorld(world),
                        _workspaceRecoveryStore,
                        resolver);
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
