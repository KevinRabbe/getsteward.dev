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
    MakeMyCopy
}

internal sealed class WorldHistoryDialog : Window
{
    private readonly ListBox _historyList;
    private readonly Button _restoreButton;
    private readonly Button _copyButton;

    public WorldHistoryDialog(
        string worldName,
        WorldHistorySnapshot history,
        RevisionId currentRevisionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(worldName);
        ArgumentNullException.ThrowIfNull(history);

        Title = $"{DesktopText.History} — {worldName}";
        Width = 720;
        Height = 640;
        MinWidth = 620;
        MinHeight = 520;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        Background = (Brush)Application.Current.FindResource("AppBackgroundBrush");
        Foreground = (Brush)Application.Current.FindResource("TextBrush");

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
            var entry = new WorldHistoryDialogEntry(
                revision,
                IsCurrent: revision.Id == currentRevisionId);
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
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        if (history.HasOlderRevisions)
        {
            var boundedNotice = new TextBlock
            {
                Text = DesktopText.OlderHistoryNotShown,
                MaxWidth = 340,
                VerticalAlignment = VerticalAlignment.Center,
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap
            };
            boundedNotice.SetResourceReference(TextBlock.ForegroundProperty, "MutedTextBrush");
            Grid.SetColumn(boundedNotice, 0);
            footer.Children.Add(boundedNotice);
        }

        var actions = new StackPanel
        {
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
        actions.Children.Add(closeButton);

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
        actions.Children.Add(_copyButton);

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
        actions.Children.Add(_restoreButton);

        Grid.SetColumn(actions, 1);
        footer.Children.Add(actions);
        Grid.SetRow(footer, 2);
        root.Children.Add(footer);

        Content = root;
        UpdateActionState();
    }

    public WorldHistoryDialogAction RequestedAction { get; private set; }

    public StateRevision? SelectedRevision
        => (_historyList.SelectedItem as ListBoxItem)?.Tag is WorldHistoryDialogEntry entry
            ? entry.Revision
            : null;

    private ListBoxItem CreateHistoryItem(WorldHistoryDialogEntry entry)
    {
        var title = new TextBlock
        {
            Text = entry.IsCurrent ? DesktopText.CurrentState : DesktopText.EarlierSave,
            FontSize = 15,
            FontWeight = FontWeights.SemiBold
        };

        var localTime = entry.Revision.CreatedAt.ToLocalTime();
        var detailText = localTime.ToString("f", CultureInfo.CurrentCulture);
        if (!string.IsNullOrWhiteSpace(entry.Revision.CreatedBy?.DisplayName))
        {
            detailText += $" · {entry.Revision.CreatedBy.DisplayName}";
        }

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
        AutomationProperties.SetName(item, $"{title.Text}, {detailText}");
        return item;
    }

    private void UpdateActionState()
    {
        var selected = (_historyList.SelectedItem as ListBoxItem)?.Tag as WorldHistoryDialogEntry;
        _copyButton.IsEnabled = selected is not null;
        _restoreButton.IsEnabled = selected is { IsCurrent: false };
    }

    private void Complete(WorldHistoryDialogAction action)
    {
        if (SelectedRevision is null)
        {
            return;
        }

        RequestedAction = action;
        DialogResult = true;
    }

    private sealed record WorldHistoryDialogEntry(StateRevision Revision, bool IsCurrent);
}
