using System.ComponentModel;
using System.Windows.Automation;
using SharedWorlds.Core.Abstractions;

namespace SharedWorlds.Desktop;

public partial class MainWindow
{
    private void InitializeGameTechnicalReadinessUi()
    {
        // Capability truth is useful as progressive-disclosure help, but it is not normal game-page
        // content. Keep it on the existing heading's tooltip/UIA help instead of adding another
        // permanent technical sentence underneath the game name.
        var headingDescriptor = DependencyPropertyDescriptor.FromProperty(
            System.Windows.Controls.TextBlock.TextProperty,
            typeof(System.Windows.Controls.TextBlock));
        headingDescriptor?.AddValueChanged(
            SelectedGameNameText,
            (_, _) => UpdateGameTechnicalReadinessSummary());

        UpdateGameTechnicalReadinessSummary();
    }

    private void UpdateGameTechnicalReadinessSummary()
    {
        if (_selectedGameAdapterId is null ||
            !TryGetAdapter(_selectedGameAdapterId, out var adapter))
        {
            SelectedGameNameText.ToolTip = null;
            AutomationProperties.SetHelpText(SelectedGameNameText, string.Empty);
            return;
        }

        var summary = FormatGameTechnicalReadiness(adapter.Capabilities);
        SelectedGameNameText.ToolTip = summary;
        AutomationProperties.SetHelpText(SelectedGameNameText, summary);
    }

    private static string FormatGameTechnicalReadiness(GameAdapterCapabilities capabilities)
    {
        var managedActions = new List<string>(5);
        if (capabilities.HasFlag(GameAdapterCapabilities.AutomaticLocalLaunch))
        {
            managedActions.Add(DesktopText.StartWorld);
        }

        if (capabilities.HasFlag(GameAdapterCapabilities.AutomaticHostLaunch))
        {
            managedActions.Add(DesktopText.HostWorld);
        }

        if (capabilities.HasFlag(GameAdapterCapabilities.AutomaticClientJoin))
        {
            managedActions.Add(DesktopText.Join);
        }

        if (capabilities.HasFlag(GameAdapterCapabilities.AutomaticHostStop))
        {
            managedActions.Add(DesktopText.StopAndSave);
        }

        if (capabilities.HasFlag(GameAdapterCapabilities.NativeWorldCreation))
        {
            managedActions.Add(DesktopText.CreateWorld);
        }

        var actionSummary = managedActions.Count == 0
            ? DesktopText.NoManagedPlayActions
            : string.Format(
                DesktopText.AdapterSupportsFormat,
                string.Join(", ", managedActions));

        return $"{DesktopText.WorldStateImportSupported} • {actionSummary}";
    }
}
