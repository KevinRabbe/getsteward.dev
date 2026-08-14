using SharedWorlds.Core.Storage;
using SharedWorlds.Core.Worlds;

namespace SharedWorlds.Desktop;

public partial class MainWindow
{
    /// <summary>
    /// Builds the prepared-runtime recovery resolver from the one already-resolved Desktop storage
    /// layout. Recovery code must use this authority rather than reconstructing app-data paths or
    /// trusting WorkspaceRecoveryRecord.WorkingDirectory directly.
    /// </summary>
    private static PreparedWorldRecoveryResolver CreatePreparedWorldRecoveryResolver()
    {
        var layout = new DesktopStorageLayout(DesktopLocalDataRoot.RequireResolvedRoot());
        return new PreparedWorldRecoveryResolver(
            new ManagedWorkspaceStorage(layout.ManagedWorkspacesRoot));
    }
}
