using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using SharedWorlds.Core.Domain;
using SharedWorlds.Core.Worlds;

namespace SharedWorlds.Desktop;

public partial class MainWindow
{
    private Button? _historyButton;
    private bool _worldHistoryUiInitialized;

    internal void InitializeWorldHistoryUi()
    {
        if (_worldHistoryUiInitialized || ShareButton.Parent is not Panel actions)
        {
            return;
        }

        _worldHistoryUiInitialized = true;
        _historyButton = new Button
        {
            Content = DesktopText.History,
            Margin = new Thickness(0, 0, 10, 10),
            MinWidth = 106,
            Height = 42,
            Visibility = Visibility.Collapsed,
            IsEnabled = false
        };
        if (TryFindResource("GhostButtonStyle") is Style ghostStyle)
        {
            _historyButton.Style = ghostStyle;
        }
        AutomationProperties.SetName(_historyButton, DesktopText.History);
        AutomationProperties.SetHelpText(
            _historyButton,
            "Open earlier safely committed versions of this local World.");
        _historyButton.Click += HistoryButton_Click;
        actions.Children.Add(_historyButton);

        WorldList.SelectionChanged += (_, _) => UpdateWorldHistoryActionState();
        WorldList.IsEnabledChanged += (_, _) => UpdateWorldHistoryActionState();
        UpdateWorldHistoryActionState();
    }

    private void UpdateWorldHistoryActionState()
    {
        if (!_worldHistoryUiInitialized || _historyButton is null)
        {
            return;
        }

        var world = _selectedWorld;
        var isSupportedLocalWorld = world is not null &&
                                    world.SharingMode == WorldSharingMode.LocalOnly &&
                                    !_remoteWorldIds.Contains(world.Id) &&
                                    world.CurrentStateRevisionId is not null;
        _historyButton.Visibility = isSupportedLocalWorld
            ? Visibility.Visible
            : Visibility.Collapsed;

        var responsibilityClear = _responsibilityTracker.Current.Kind ==
                                  WorldLifecycleResponsibilityKind.None;
        _historyButton.IsEnabled = isSupportedLocalWorld && !_isBusy && responsibilityClear;

        var help = responsibilityClear
            ? "Open earlier safely committed versions of this local World."
            : "Finish or resolve the active World session before changing History.";
        _historyButton.ToolTip = help;
        AutomationProperties.SetHelpText(_historyButton, help);
    }

    private async void HistoryButton_Click(object sender, RoutedEventArgs e)
    {
        var world = _selectedWorld;
        if (world is null ||
            world.SharingMode != WorldSharingMode.LocalOnly ||
            _remoteWorldIds.Contains(world.Id) ||
            world.CurrentStateRevisionId is not { } currentRevisionId ||
            _responsibilityTracker.Current.Kind != WorldLifecycleResponsibilityKind.None)
        {
            UpdateWorldHistoryActionState();
            return;
        }

        try
        {
            var historyService = new WorldHistoryService(_storage);
            var history = await historyService.GetHistoryAsync(world);
            if (_selectedWorld?.Id != world.Id)
            {
                return;
            }

            var dialog = new WorldHistoryDialog(
                world.Name,
                history,
                currentRevisionId,
                world.Checkpoints ?? [])
            {
                Owner = this
            };
            if (dialog.ShowDialog() != true || dialog.SelectedRevision is not { } selectedRevision)
            {
                return;
            }

            switch (dialog.RequestedAction)
            {
                case WorldHistoryDialogAction.Restore:
                    await RestoreWorldHistoryAsync(world, selectedRevision);
                    break;

                case WorldHistoryDialogAction.MakeMyCopy:
                    await MakeWorldHistoryCopyAsync(world, selectedRevision);
                    break;

                case WorldHistoryDialogAction.NameCheckpoint:
                    await NameWorldHistoryCheckpointAsync(
                        world,
                        selectedRevision,
                        dialog.SelectedCheckpoint);
                    break;

                case WorldHistoryDialogAction.RemoveCheckpoint:
                    if (dialog.SelectedCheckpoint is { } checkpoint)
                    {
                        await RemoveWorldHistoryCheckpointAsync(
                            world,
                            selectedRevision,
                            checkpoint);
                    }
                    break;
            }
        }
        catch (Exception exception)
        {
            ShowError("Could not open World History", exception);
        }
    }

    private async Task RestoreWorldHistoryAsync(World world, StateRevision selectedRevision)
    {
        var savedAt = selectedRevision.CreatedAt
            .ToLocalTime()
            .ToString("f", CultureInfo.CurrentCulture);
        var confirmation = MessageBox.Show(
            this,
            $"Restore '{world.Name}' to the saved state from {savedAt}?{Environment.NewLine}{Environment.NewLine}" +
            "Safe World will create a new current state from that save. Every later History entry remains available.",
            $"{DesktopText.Restore} {world.Name}",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question,
            MessageBoxResult.No);
        if (confirmation != MessageBoxResult.Yes)
        {
            return;
        }

        await RunOperationAsync(
            $"Restoring {world.Name}...",
            async () =>
            {
                var service = new WorldHistoryService(_storage);
                var updated = await service.RestoreAsync(
                    world,
                    selectedRevision.Id,
                    GetLocalUser());
                _selectedWorld = updated;
                StatusText.Text = $"Restored '{updated.Name}'. Later History was preserved.";
                await RefreshUnifiedWorldsAsync(updated.Id, preserveStatus: true);
            });
    }

    private async Task MakeWorldHistoryCopyAsync(World world, StateRevision selectedRevision)
    {
        var copyName = await CreateUniqueWorldNameAsync($"{world.Name} copy");
        var savedAt = selectedRevision.CreatedAt
            .ToLocalTime()
            .ToString("f", CultureInfo.CurrentCulture);
        var confirmation = MessageBox.Show(
            this,
            $"Create '{copyName}' from the saved state on {savedAt}?{Environment.NewLine}{Environment.NewLine}" +
            "The copy will be a separate local World. Changes to either World will not affect the other.",
            DesktopText.MakeMyCopy,
            MessageBoxButton.YesNo,
            MessageBoxImage.Question,
            MessageBoxResult.No);
        if (confirmation != MessageBoxResult.Yes)
        {
            return;
        }

        await RunOperationAsync(
            $"Creating {copyName}...",
            async () =>
            {
                var service = new WorldHistoryService(_storage);
                var copy = await service.MakeIndependentCopyAsync(
                    world,
                    selectedRevision.Id,
                    copyName,
                    GetLocalUser());
                StatusText.Text = $"Created independent World '{copy.Name}' from History.";
                await RefreshUnifiedWorldsAsync(copy.Id, preserveStatus: true);
            });
    }

    private async Task NameWorldHistoryCheckpointAsync(
        World world,
        StateRevision selectedRevision,
        WorldCheckpoint? existingCheckpoint)
    {
        var dialog = new CheckpointNameDialog(world.Name, existingCheckpoint?.Name)
        {
            Owner = this
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        var checkpointName = dialog.CheckpointName;
        await RunOperationAsync(
            $"Saving checkpoint {checkpointName}...",
            async () =>
            {
                var service = new WorldCheckpointService(_storage);
                var updated = await service.SetAsync(
                    world,
                    selectedRevision.Id,
                    checkpointName,
                    GetLocalUser());
                _selectedWorld = updated;
                StatusText.Text = $"Saved checkpoint '{checkpointName}' in '{world.Name}'.";
                await RefreshUnifiedWorldsAsync(updated.Id, preserveStatus: true);
            });
    }

    private async Task RemoveWorldHistoryCheckpointAsync(
        World world,
        StateRevision selectedRevision,
        WorldCheckpoint checkpoint)
    {
        var confirmation = MessageBox.Show(
            this,
            $"Remove checkpoint '{checkpoint.Name}'?{Environment.NewLine}{Environment.NewLine}" +
            "Only the label will be removed. The saved state remains in World History.",
            DesktopText.RemoveCheckpoint,
            MessageBoxButton.YesNo,
            MessageBoxImage.Question,
            MessageBoxResult.No);
        if (confirmation != MessageBoxResult.Yes)
        {
            return;
        }

        await RunOperationAsync(
            $"Removing checkpoint {checkpoint.Name}...",
            async () =>
            {
                var service = new WorldCheckpointService(_storage);
                var updated = await service.RemoveAsync(world, selectedRevision.Id);
                _selectedWorld = updated;
                StatusText.Text = $"Removed checkpoint '{checkpoint.Name}'. The saved state remains in History.";
                await RefreshUnifiedWorldsAsync(updated.Id, preserveStatus: true);
            });
    }
}
