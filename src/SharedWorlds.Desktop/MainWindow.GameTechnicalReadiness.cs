using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using SharedWorlds.Core.Abstractions;

namespace SharedWorlds.Desktop;

public partial class MainWindow
{
    private TextBlock? _gameTechnicalReadinessText;

    private void InitializeGameTechnicalReadinessUi()
    {
        if (SelectedGameNameText.Parent is not StackPanel gameHeader)
        {
            throw new InvalidOperationException(
                "Selected game heading must remain inside the game-workspace header stack.");
        }

        _gameTechnicalReadinessText = new TextBlock
        {
            Margin = new Thickness(0, 5, 0, 0),
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Foreground = (Brush)FindResource("MutedTextBrush")
        };
        AutomationProperties.SetName(
            _gameTechnicalReadinessText,
            DesktopText.TechnicalReadiness);

        var headingIndex = gameHeader.Children.IndexOf(SelectedGameNameText);
        gameHeader.Children.Insert(headingIndex + 1, _gameTechnicalReadinessText);

        GameLibraryList.SelectionChanged += (_, _) => UpdateGameTechnicalReadinessSummary();
    }

    private void UpdateGameTechnicalReadinessSummary()
    {
        if (_gameTechnicalReadinessText is null)
        {
            return;
        }

        if (GameLibraryList.SelectedItem is not GameLibraryItem selected ||
            !TryGetAdapter(selected.AdapterId, out var adapter))
        {
            _gameTechnicalReadinessText.Text = string.Empty;
            return;
        }

        _gameTechnicalReadinessText.Text = FormatGameTechnicalReadiness(adapter.Capabilities);
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
