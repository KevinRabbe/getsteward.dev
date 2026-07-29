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
            Text = DesktopText.DeleteWorld,
            FontWeight = FontWeights.SemiBold
        };
        var description = new TextBlock
        {
            Text = DesktopText.DeleteWorldDescription,
            Margin = new Thickness(0, 6, 0, 12),
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Foreground = (Brush)FindResource("MutedTextBrush")
        };
        var button = new Button
        {
            Content = DesktopText.DeleteWorld,
            HorizontalAlignment = HorizontalAlignment.Left,
            Padding = new Thickness(12, 6, 12, 6),
            FontWeight = FontWeights.SemiBold
        };
        AutomationProperties.SetName(button, DesktopText.DeleteWorld);
        AutomationProperties.SetHelpText(
            button,
            "Delete Safe World's local managed copy and revision history for the selected World.");

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
            _deleteWorldButton is null)
        {
            return;
        }

        var world = _selectedWorld;
        var isLocalOnly = world is not null &&
                          world.SharingMode == WorldSharingMode.LocalOnly &&
                          !_remoteWorldIds.Contains(world.Id);
        _deleteWorldPanel.Visibility = isLocalOnly
            ? Visibility.Visible
            : Visibility.Collapsed;

        if (!isLocalOnly || world is null)
        {
            _deleteWorldButton.IsEnabled = false;
            return;
        }

        var responsibility = _responsibilityTracker.Current;
        var selectedOwnsResponsibility = responsibility.Kind != WorldLifecycleResponsibilityKind.None &&
                                         responsibility.WorldId == world.Id;
        _deleteWorldButton.IsEnabled = !_isBusy && !selectedOwnsResponsibility;

        var help = selectedOwnsResponsibility
            ? "Resolve this World's active or recovery responsibility before deleting its managed copy."
            : "Delete Safe World's managed copy and revision history. The game's own save folder is not modified.";
        _deleteWorldButton.ToolTip = help;
        AutomationProperties.SetHelpText(_deleteWorldButton, help);
    }

    private async void DeleteWorldButton_Click(object sender, RoutedEventArgs e)
    {
        var world = _selectedWorld;
        if (world is null ||
            world.SharingMode != WorldSharingMode.LocalOnly ||
            _remoteWorldIds.Contains(world.Id))
        {
            return;
        }

        var responsibility = _responsibilityTracker.Current;
        if (responsibility.Kind != WorldLifecycleResponsibilityKind.None &&
            responsibility.WorldId == world.Id)
        {
            StatusText.Text = "Resolve this World's active or recovery responsibility before deleting it.";
            UpdateWorldDeletionActionState();
            return;
        }

        var confirmation = MessageBox.Show(
            this,
            $"Delete '{world.Name}' from Safe World on this PC?{Environment.NewLine}{Environment.NewLine}" +
            "Safe World's managed copy and its revision history will be deleted. " +
            "A save in the game's own save folder is not deleted." +
            $"{Environment.NewLine}{Environment.NewLine}This cannot be undone.",
            DesktopText.DeleteWorld,
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
            $"Deleting {worldName}...",
            async () =>
            {
                if (!await _storage.DeleteWorldAsync(worldId))
                {
                    throw new InvalidOperationException(
                        $"World '{worldName}' is no longer present in Safe World's local managed storage.");
                }

                _selectedWorld = null;
                WorldList.SelectedItem = null;
                await RefreshUnifiedWorldsAsync(preserveStatus: true);
                StatusText.Text = $"Deleted '{worldName}' from Safe World.";
            });

        UpdateWorldDeletionActionState();
    }
}
