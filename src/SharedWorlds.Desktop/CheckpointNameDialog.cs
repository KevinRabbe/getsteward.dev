using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using SharedWorlds.Core.Worlds;

namespace SharedWorlds.Desktop;

internal sealed class CheckpointNameDialog : Window
{
    private readonly TextBox _nameBox;
    private readonly Button _saveButton;
    private readonly TextBlock _countText;

    public CheckpointNameDialog(string worldName, string? existingName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(worldName);

        Title = string.IsNullOrWhiteSpace(existingName)
            ? $"{DesktopText.NameCheckpoint} — {worldName}"
            : $"{DesktopText.RenameCheckpoint} — {worldName}";
        Width = 480;
        Height = 260;
        MinWidth = 420;
        MinHeight = 240;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        Background = (Brush)Application.Current.FindResource("AppBackgroundBrush");
        Foreground = (Brush)Application.Current.FindResource("TextBrush");

        var root = new Grid
        {
            Margin = new Thickness(28)
        };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var heading = new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(existingName)
                ? DesktopText.NameCheckpoint
                : DesktopText.RenameCheckpoint,
            FontSize = 23,
            FontWeight = FontWeights.Bold
        };
        Grid.SetRow(heading, 0);
        root.Children.Add(heading);

        var description = new TextBlock
        {
            Text = "Give this saved state a memorable name. The checkpoint is only a label; it does not create another copy of the World.",
            Margin = new Thickness(0, 7, 0, 18),
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap
        };
        description.SetResourceReference(TextBlock.ForegroundProperty, "MutedTextBrush");
        Grid.SetRow(description, 1);
        root.Children.Add(description);

        _nameBox = new TextBox
        {
            Text = existingName ?? string.Empty,
            MaxLength = WorldCheckpointService.MaximumCheckpointNameLength,
            Height = 42,
            Padding = new Thickness(11, 8, 11, 8),
            VerticalContentAlignment = VerticalAlignment.Center
        };
        AutomationProperties.SetName(_nameBox, DesktopText.CheckpointName);
        _nameBox.TextChanged += (_, _) => UpdateState();
        _nameBox.KeyDown += (_, args) =>
        {
            if (args.Key == System.Windows.Input.Key.Enter && _saveButton.IsEnabled)
            {
                Complete();
                args.Handled = true;
            }
        };
        Grid.SetRow(_nameBox, 2);
        root.Children.Add(_nameBox);

        _countText = new TextBlock
        {
            Margin = new Thickness(0, 6, 0, 18),
            HorizontalAlignment = HorizontalAlignment.Right,
            FontSize = 11
        };
        _countText.SetResourceReference(TextBlock.ForegroundProperty, "MutedTextBrush");
        Grid.SetRow(_countText, 3);
        root.Children.Add(_countText);

        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right
        };
        var cancelButton = new Button
        {
            Content = DesktopText.Close,
            MinWidth = 92,
            Height = 42,
            IsCancel = true
        };
        cancelButton.Click += (_, _) => Close();
        actions.Children.Add(cancelButton);

        _saveButton = new Button
        {
            Content = DesktopText.Save,
            MinWidth = 104,
            Height = 42,
            Margin = new Thickness(10, 0, 0, 0),
            IsDefault = true
        };
        if (Application.Current.TryFindResource("PrimaryButtonStyle") is Style primaryStyle)
        {
            _saveButton.Style = primaryStyle;
        }
        _saveButton.Click += (_, _) => Complete();
        actions.Children.Add(_saveButton);
        Grid.SetRow(actions, 4);
        root.Children.Add(actions);

        base.Content = root;
        Loaded += (_, _) =>
        {
            _nameBox.Focus();
            _nameBox.SelectAll();
        };
        UpdateState();
    }

    public string ResultName => _nameBox.Text.Trim();

    private void UpdateState()
    {
        var length = _nameBox.Text.Length;
        _countText.Text = $"{length}/{WorldCheckpointService.MaximumCheckpointNameLength}";
        _saveButton.IsEnabled = !string.IsNullOrWhiteSpace(_nameBox.Text) &&
                                !_nameBox.Text.Any(char.IsControl);
    }

    private void Complete()
    {
        if (!_saveButton.IsEnabled)
        {
            return;
        }

        DialogResult = true;
    }
}
