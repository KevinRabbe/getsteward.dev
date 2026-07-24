using System.Windows;
using System.Windows.Automation;

namespace SharedWorlds.Desktop;

public partial class MainWindow
{
    private bool _importAccessibilityInitialized;

    internal void InitializeImportAccessibility()
    {
        if (_importAccessibilityInitialized)
        {
            return;
        }

        _importAccessibilityInitialized = true;

        if (_importSearchBox is not null)
        {
            AutomationProperties.SetName(_importSearchBox, "Search detected Worlds");
            AutomationProperties.SetHelpText(
                _importSearchBox,
                "Search the selected game's detected Worlds by World name or native World ID.");
        }

        if (_importBrowserList is not null)
        {
            AutomationProperties.SetName(_importBrowserList, "Detected Worlds");
        }

        if (_importResultText is not null)
        {
            AutomationProperties.SetLiveSetting(_importResultText, AutomationLiveSetting.Polite);
        }

        if (_importBrowserImportButton is not null)
        {
            AutomationProperties.SetHelpText(
                _importBrowserImportButton,
                "Import the selected detected World into Steward as a private World on this PC.");
        }

        if (_importBrowserScanButton is not null)
        {
            AutomationProperties.SetHelpText(
                _importBrowserScanButton,
                "Scan installed supported games again for importable Worlds.");
        }

        if (_importWorkspace is not null)
        {
            _importWorkspace.IsVisibleChanged += ImportWorkspace_IsVisibleChanged;
        }
    }

    private void ImportWorkspace_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is true || _isBusy)
        {
            return;
        }

        // Manual close returns keyboard users to the action that opened the workspace. Successful
        // import closes while busy and deliberately leaves focus to the refreshed World surface.
        OpenImportButton.Focus();
    }
}
