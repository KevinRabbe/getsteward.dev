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
                    // A missing local installation is acceptable only when adapter-owned workspace
                    // cleanup already succeeded and the durable journal is the sole remaining work.
                    var installation = (await adapter.DiscoverInstallationsAsync()).FirstOrDefault();
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
