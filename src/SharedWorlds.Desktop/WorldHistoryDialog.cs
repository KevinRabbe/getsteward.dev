using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using SharedWorlds.Core.Domain;
using SharedWorlds.Core.Worlds;

namespace SharedWorlds.Desktop;

internal enum WorldHistoryDialogAction
{
    None,
    Restore,
    MakeMyCopy,
    NameCheckpoint,
    RemoveCheckpoint,
    ManageStorage
}

internal sealed class WorldHistoryDialog : Window
{
    private readonly ListBox _historyList;
    private readonly Button _restoreButton;
    private readonly Button _copyButton;
    private readonly Button _checkpointButton;
    private readonly Button _removeCheckpointButton;

    public WorldHistoryDialog(
        string worldName,
        WorldHistorySnapshot history,
        RevisionId currentRevisionId,
        IReadOnlyList<WorldCheckpoint> checkpoints,
        IReadOnlyDictionary<RevisionId, bool> payloadAvailability)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(worldName);
        ArgumentNullException.ThrowIfNull(history);
        ArgumentNullException.ThrowIfNull(checkpoints);
        ArgumentNullException.ThrowIfNull(payloadAvailability);

        Title = $"{DesktopText.History} — {worldName}";
        Width = 800;
        Height = 660;
        MinWidth = 720;
        MinHeight = 540;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        Background = (Brush)Application.Current.FindResource("AppBackgroundBrush");
        Foreground = (Brush)Application.Current.FindResource("TextBrush");

        var checkpointsByRevision = checkpoints.ToDictionary(
            checkpoint => checkpoint.StateRevisionId);
        var root = new Grid
        {
            Margin = new Thickness(30)
        };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var header = new StackPanel();
        header.Children.Add(new TextBlock
        {
            Text = DesktopText.History,
            FontSize = 28,
            FontWeight = FontWeights.Bold
        });
        var description = new TextBlock
        {
            Text = DesktopText.WorldHistoryDescription,
            Margin = new Thickness(0, 7, 0, 22),
            FontSize = 13,
            TextWrapping = TextWrapping.Wrap
        };
        description.SetResourceReference(TextBlock.ForegroundProperty, "MutedTextBrush");
        header.Children.Add(description);
        Grid.SetRow(header, 0);
        root.Children.Add(header);

        _historyList = new ListBox
        {
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent
        };
        AutomationProperties.SetName(_historyList, $"{worldName} {DesktopText.History}");
        _historyList.SelectionChanged += (_, _) => UpdateActionState();

        foreach (var revision in history.Revisions)
        {
            checkpointsByRevision.TryGetValue(revision.Id, out var checkpoint);
            var entry = new WorldHistoryDialogEntry(
                revision,
                IsCurrent: revision.Id == currentRevisionId,
                checkpoint,
                IsPayloadAvailable: payloadAvailability.TryGetValue(revision.Id, out var available) && available);
            _historyList.Items.Add(CreateHistoryItem(entry));
        }

        if (_historyList.Items.Count > 0)
        {
            _historyList.SelectedIndex = 0;
        }

        var historyShell = new Border
        {
            Padding = new Thickness(6),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(14),
            Child = _historyList
        };
        historyShell.SetResourceReference(Border.BackgroundProperty, "PanelBrush");
        historyShell.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");
        Grid.SetRow(historyShell, 1);
        root.Children.Add(historyShell);

        var footer = new Grid
        {
            Margin = new Thickness(0, 20, 0, 0)
        };
        footer.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        footer.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var metadataRow = new Grid();
        metadataRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        metadataRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var storageActions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center
        };
        var manageStorageButton = new Button
        {
            Content = DesktopText.ManageHistoryStorage,
            MinWidth = 136,
            Height = 38
        };
        AutomationProperties.SetHelpText(
            manageStorageButton,
            "Reclaim space from older uncheckpointed saves while preserving their History entries.");
        manageStorageButton.Click += (_, _) => Complete(WorldHistoryDialogAction.ManageStorage);
        storageActions.Children.Add(manageStorageButton);

        if (history.HasOlderRevisions)
        {
            var boundedNotice = new TextBlock
            {
                Text = DesktopText.OlderHistoryNotShown,
                MaxWidth = 310,
                Margin = new Thickness(12, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap
            };
            boundedNotice.SetResourceReference(TextBlock.ForegroundProperty, "MutedTextBrush");
            storageActions.Children.Add(boundedNotice);
        }

        Grid.SetColumn(storageActions, 0);
        metadataRow.Children.Add(storageActions);

        var checkpointActions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right
        };
        _removeCheckpointButton = new Button
        {
            Content = DesktopText.RemoveCheckpoint,
            MinWidth = 146,
            Height = 38,
            Visibility = Visibility.Collapsed
        };
        AutomationProperties.SetHelpText(
            _removeCheckpointButton,
            "Remove only the human label. The saved state remains in World History.");
        _removeCheckpointButton.Click += (_, _) => Complete(WorldHistoryDialogAction.RemoveCheckpoint);
        checkpointActions.Children.Add(_removeCheckpointButton);

        _checkpointButton = new Button
        {
            Content = DesktopText.NameCheckpoint,
            MinWidth = 146,
            Height = 38,
            Margin = new Thickness(10, 0, 0, 0)
        };
        AutomationProperties.SetHelpText(
            _checkpointButton,
            "Give the selected saved state a memorable label without creating another copy.");
        _checkpointButton.Click += (_, _) => Complete(WorldHistoryDialogAction.NameCheckpoint);
        checkpointActions.Children.Add(_checkpointButton);
        Grid.SetColumn(checkpointActions, 1);
        metadataRow.Children.Add(checkpointActions);
        Grid.SetRow(metadataRow, 0);
        footer.Children.Add(metadataRow);

        var mainActions = new StackPanel
        {
            Margin = new Thickness(0, 14, 0, 0),
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right
        };
        var closeButton = new Button
        {
            Content = DesktopText.Close,
            MinWidth = 92,
            Height = 42,
            IsCancel = true
        };
        closeButton.Click += (_, _) => Close();
        mainActions.Children.Add(closeButton);

        _copyButton = new Button
        {
            Content = DesktopText.MakeMyCopy,
            MinWidth = 138,
            Height = 42,
            Margin = new Thickness(10, 0, 0, 0)
        };
        AutomationProperties.SetHelpText(
            _copyButton,
            "Create a separate local World from the selected saved state. The original World and its History remain unchanged.");
        _copyButton.Click += (_, _) => Complete(WorldHistoryDialogAction.MakeMyCopy);
        mainActions.Children.Add(_copyButton);

        _restoreButton = new Button
        {
            Content = DesktopText.Restore,
            MinWidth = 108,
            Height = 42,
            Margin = new Thickness(10, 0, 0, 0),
            IsDefault = true
        };
        if (Application.Current.TryFindResource("PrimaryButtonStyle") is Style primaryStyle)
        {
            _restoreButton.Style = primaryStyle;
        }
        AutomationProperties.SetHelpText(
            _restoreButton,
            "Create a new current saved state from the selected earlier save. Later History is preserved.");
        _restoreButton.Click += (_, _) => Complete(WorldHistoryDialogAction.Restore);
        mainActions.Children.Add(_restoreButton);
        Grid.SetRow(mainActions, 1);
        footer.Children.Add(mainActions);

        Grid.SetRow(footer, 2);
        root.Children.Add(footer);

        Content = root;
        UpdateActionState();
    }

    public WorldHistoryDialogAction RequestedAction { get; private set; }

    public StateRevision? SelectedRevision
        => SelectedEntry?.Revision;

    public WorldCheckpoint? SelectedCheckpoint
        => SelectedEntry?.Checkpoint;

    private WorldHistoryDialogEntry? SelectedEntry
        => (_historyList.SelectedItem as ListBoxItem)?.Tag as WorldHistoryDialogEntry;

    private ListBoxItem CreateHistoryItem(WorldHistoryDialogEntry entry)
    {
        var titleText = entry.Checkpoint?.Name ??
                        (entry.IsCurrent ? DesktopText.CurrentState : DesktopText.EarlierSave);
        var title = new TextBlock
        {
            Text = titleText,
            FontSize = 15,
            FontWeight = FontWeights.SemiBold
        };

        var localTime = entry.Revision.CreatedAt.ToLocalTime();
        var detailParts = new List<string>();
        if (entry.Checkpoint is not null)
        {
            detailParts.Add(DesktopText.Checkpoint);
        }

        detailParts.Add(entry.IsCurrent ? DesktopText.CurrentState : DesktopText.EarlierSave);
        detailParts.Add(entry.IsPayloadAvailable
            ? DesktopText.AvailableSavedState
            : DesktopText.SpaceSavingOnly);
        detailParts.Add(localTime.ToString("f", CultureInfo.CurrentCulture));
        if (!string.IsNullOrWhiteSpace(entry.Revision.CreatedBy?.DisplayName))
        {
            detailParts.Add(entry.Revision.CreatedBy.DisplayName);
        }

        var detailText = string.Join(" · ", detailParts);
        var detail = new TextBlock
        {
            Text = detailText,
            Margin = new Thickness(0, 5, 0, 0),
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap
        };
        detail.SetResourceReference(TextBlock.ForegroundProperty, "MutedTextBrush");

        var content = new StackPanel();
        content.Children.Add(title);
        content.Children.Add(detail);

        var item = new ListBoxItem
        {
            Tag = entry,
            Content = content,
            Padding = new Thickness(16, 14, 16, 14),
            Margin = new Thickness(0, 0, 0, 4),
            HorizontalContentAlignment = HorizontalAlignment.Stretch
        };
        AutomationProperties.SetName(item, $"{titleText}, {detailText}");
        return item;
    }

    private void UpdateActionState()
    {
        var selected = SelectedEntry;
        _copyButton.IsEnabled = selected is { IsPayloadAvailable: true };
        _restoreButton.IsEnabled = selected is { IsCurrent: false, IsPayloadAvailable: true };
        _checkpointButton.IsEnabled = selected is not null;
        _checkpointButton.Content = selected?.Checkpoint is null
            ? DesktopText.NameCheckpoint
            : DesktopText.RenameCheckpoint;
        _removeCheckpointButton.Visibility = selected?.Checkpoint is null
            ? Visibility.Collapsed
            : Visibility.Visible;
        _removeCheckpointButton.IsEnabled = selected?.Checkpoint is not null;

        var unavailableHelp =
            "This History entry is preserved as metadata, but its saved-state bytes were removed to reclaim space.";
        _copyButton.ToolTip = selected is { IsPayloadAvailable: false }
            ? unavailableHelp
            : null;
        _restoreButton.ToolTip = selected is { IsPayloadAvailable: false }
            ? unavailableHelp
            : null;
    }

    private void Complete(WorldHistoryDialogAction action)
    {
        if (action != WorldHistoryDialogAction.ManageStorage && SelectedRevision is null)
        {
            return;
        }

        RequestedAction = action;
        DialogResult = true;
    }

    private sealed record WorldHistoryDialogEntry(
        StateRevision Revision,
        bool IsCurrent,
        WorldCheckpoint? Checkpoint,
        bool IsPayloadAvailable);
}
