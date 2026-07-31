using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using SharedWorlds.Core.Domain;
using SharedWorlds.Core.Worlds;

namespace SharedWorlds.Desktop;

public partial class MainWindow
{
    private StackPanel? _deleteWorldPanel;
    private TextBlock? _deleteWorldDescription;
    private Button? _deleteWorldButton;
    private bool _worldDeletionUiInitialized;

    internal void InitializeWorldDeletionUi()
    {
        if (_worldDeletionUiInitialized ||
            KeepExactGameVersionCheckBox.Parent is not StackPanel settings)
        {
            return;
        }

        _worldDeletionUiInitialized = true;

        var separator = new Border
        {
            Height = 1,
            Margin = new Thickness(0, 18, 0, 16),
            Background = (Brush)FindResource("BorderBrush")
        };
        var heading = new TextBlock
        {
            Text = DesktopText.DangerZone,
            FontWeight = FontWeights.SemiBold
        };
        var description = new TextBlock
        {
            Margin = new Thickness(0, 6, 0, 12),
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Foreground = (Brush)FindResource("MutedTextBrush")
        };
        var button = new Button
        {
            HorizontalAlignment = HorizontalAlignment.Left,
            Padding = new Thickness(12, 6, 12, 6),
            FontWeight = FontWeights.SemiBold
        };

        var panel = new StackPanel
        {
            Visibility = Visibility.Collapsed
        };
        panel.Children.Add(separator);
        panel.Children.Add(heading);
        panel.Children.Add(description);
        panel.Children.Add(button);
        settings.Children.Add(panel);

        _deleteWorldPanel = panel;
        _deleteWorldDescription = description;
        _deleteWorldButton = button;
        button.Click += DeleteWorldButton_Click;
        WorldList.SelectionChanged += (_, _) => UpdateWorldDeletionActionState();
        WorldList.IsEnabledChanged += (_, _) => UpdateWorldDeletionActionState();

        UpdateWorldDeletionActionState();
    }

    private void UpdateWorldDeletionActionState()
    {
        if (!_worldDeletionUiInitialized ||
            _deleteWorldPanel is null ||
            _deleteWorldDescription is null ||
            _deleteWorldButton is null)
        {
            return;
        }

        var world = _selectedWorld;
        var isLocalCatalogRecord = world is not null && !_remoteWorldIds.Contains(world.Id);
        _deleteWorldPanel.Visibility = isLocalCatalogRecord
            ? Visibility.Visible
            : Visibility.Collapsed;

        if (!isLocalCatalogRecord || world is null)
        {
            _deleteWorldButton.IsEnabled = false;
            return;
        }

        var incompleteSharedRecord = world.SharingMode == WorldSharingMode.Shared;
        _deleteWorldButton.Content = incompleteSharedRecord
            ? "Remove from Safe World"
            : DesktopText.DeleteWorld;
        _deleteWorldDescription.Text = incompleteSharedRecord
            ? "Remove this incomplete shared World record and its local revision history from this PC. This does not delete a shared World for other people."
            : DesktopText.DeleteWorldDescription;
        AutomationProperties.SetName(
            _deleteWorldButton,
            incompleteSharedRecord ? "Remove from Safe World" : DesktopText.DeleteWorld);

        var responsibility = _responsibilityTracker.Current;
        var selectedOwnsResponsibility = responsibility.Kind != WorldLifecycleResponsibilityKind.None &&
                                         responsibility.WorldId == world.Id;
        _deleteWorldButton.IsEnabled = !_isBusy && !selectedOwnsResponsibility;

        var help = selectedOwnsResponsibility
            ? "Continue from the last safe state or otherwise resolve this World's responsibility before removing it."
            : incompleteSharedRecord
                ? "Remove the incomplete local shared-World record from this PC. This does not delete a connected shared World for other people."
                : "Delete Safe World's managed copy and revision history. The game's own save folder is not modified.";
        _deleteWorldButton.ToolTip = help;
        AutomationProperties.SetHelpText(_deleteWorldButton, help);
    }

    private async void DeleteWorldButton_Click(object sender, RoutedEventArgs e)
    {
        var world = _selectedWorld;
        if (world is null || _remoteWorldIds.Contains(world.Id))
        {
            return;
        }

        var responsibility = _responsibilityTracker.Current;
        if (responsibility.Kind != WorldLifecycleResponsibilityKind.None &&
            responsibility.WorldId == world.Id)
        {
            StatusText.Text =
                "Resolve this World's active or recovery responsibility before removing it from Safe World.";
            UpdateWorldDeletionActionState();
            return;
        }

        var incompleteSharedRecord = world.SharingMode == WorldSharingMode.Shared;
        var actionTitle = incompleteSharedRecord
            ? "Remove from Safe World"
            : DesktopText.DeleteWorld;
        var consequence = incompleteSharedRecord
            ? "Safe World will remove this incomplete shared World record and its local revision history from this PC. " +
              "This does not delete a shared World for other people. If an incomplete backend record was already created, it may still require remote cleanup later."
            : "Safe World's managed copy and its revision history will be deleted. " +
              "A save in the game's own save folder is not deleted.";

        var confirmation = MessageBox.Show(
            this,
            $"{actionTitle} for '{world.Name}'?{Environment.NewLine}{Environment.NewLine}" +
            consequence +
            $"{Environment.NewLine}{Environment.NewLine}This cannot be undone on this PC.",
            actionTitle,
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);
        if (confirmation != MessageBoxResult.Yes)
        {
            return;
        }

        var worldId = world.Id;
        var worldName = world.Name;
        await RunUnifiedOperationAsync(
            $"Removing {worldName}...",
            async () =>
            {
                if (!await _storage.DeleteWorldAsync(worldId))
                {
                    throw new InvalidOperationException(
                        $"World '{worldName}' is no longer present in Safe World's local managed storage.");
                }

                _remoteIncompleteWorldIds.Remove(worldId);
                _selectedWorld = null;
                WorldList.SelectedItem = null;
                await RefreshUnifiedWorldsAsync(preserveStatus: true);
                StatusText.Text = incompleteSharedRecord
                    ? $"Removed incomplete shared World '{worldName}' from this PC."
                    : $"Deleted '{worldName}' from Safe World.";
            });

        UpdateWorldDeletionActionState();
    }
}
