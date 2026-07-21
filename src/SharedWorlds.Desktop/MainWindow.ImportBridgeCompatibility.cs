using System.Windows;

namespace SharedWorlds.Desktop;

public partial class MainWindow
{
    // Temporary UI-1 compatibility hook: UnifiedImportBrowser still detaches this historical
    // compact-import handler before attaching the real Import workspace handler. Nothing attaches
    // this method anymore, so it cannot execute product behavior. Remove it together with the
    // compact ImportPanel/ImportCandidateComboBox anchors.
    private void UnifiedOpenImportButton_Click(object sender, RoutedEventArgs e)
    {
    }
}
