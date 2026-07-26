using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using SharedWorlds.Core.Abstractions;

namespace SharedWorlds.Desktop;

internal sealed record CreateWorldOption(
    IGameAdapter Adapter,
    GameInstallation Installation)
{
    public string DisplayName => Adapter.DisplayName;
}

internal sealed class CreateWorldDialog : Window
{
    private readonly ComboBox _game = new();
    private readonly TextBox _worldName = new();
    private readonly Button _createButton = new()
    {
        Content = "Create World",
        Padding = new Thickness(16, 6, 16, 6),
        IsDefault = true
    };

    public CreateWorldDialog(IReadOnlyList<CreateWorldOption> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.Count == 0)
        {
            throw new ArgumentException(
                "At least one installed native-creation game is required.",
                nameof(options));
        }

        Title = "Create new World";
        Width = 520;
        SizeToContent = SizeToContent.Height;
        MinWidth = 420;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        UseLayoutRounding = true;
        SnapsToDevicePixels = true;
        Background = (Brush)Application.Current.FindResource("AppBackgroundBrush");
        Foreground = (Brush)Application.Current.FindResource("TextBrush");
        Content = BuildContent(options);

        _game.SelectionChanged += (_, _) =>
        {
            if (_game.SelectedItem is CreateWorldOption selected &&
                string.IsNullOrWhiteSpace(_worldName.Text))
            {
                _worldName.Text = $"{selected.DisplayName} World";
                _worldName.SelectAll();
            }

            UpdateAction();
        };
        _worldName.TextChanged += (_, _) => UpdateAction();
        _createButton.Click += (_, _) =>
        {
            SelectedOption = _game.SelectedItem as CreateWorldOption;
            WorldName = _worldName.Text.Trim();
            DialogResult = true;
        };
        Loaded += (_, _) =>
        {
            _game.SelectedIndex = 0;
            _worldName.Focus();
            _worldName.SelectAll();
            UpdateAction();
        };
    }

    public CreateWorldOption? SelectedOption { get; private set; }
    public string? WorldName { get; private set; }

    private UIElement BuildContent(IReadOnlyList<CreateWorldOption> options)
    {
        var root = new Grid { Margin = new Thickness(20) };
        for (var row = 0; row < 6; row++)
        {
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        }

        var heading = new StackPanel();
        heading.Children.Add(new TextBlock
        {
            Text = "Create new World",
            FontSize = 22,
            FontWeight = FontWeights.SemiBold
        });
        heading.Children.Add(new TextBlock
        {
            Text = "Steward will ask the game to create its own native World, then manage that World normally.",
            Margin = new Thickness(0, 6, 0, 16),
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.78
        });
        Grid.SetRow(heading, 0);
        root.Children.Add(heading);

        var gameLabel = new TextBlock
        {
            Text = "Game",
            Margin = new Thickness(0, 0, 0, 6)
        };
        Grid.SetRow(gameLabel, 1);
        root.Children.Add(gameLabel);

        _game.ItemsSource = options;
        _game.DisplayMemberPath = nameof(CreateWorldOption.DisplayName);
        _game.Padding = new Thickness(8, 6, 8, 6);
        AutomationProperties.SetName(_game, "Game");
        Grid.SetRow(_game, 2);
        root.Children.Add(_game);

        var nameLabel = new TextBlock
        {
            Text = "World name",
            Margin = new Thickness(0, 16, 0, 6)
        };
        Grid.SetRow(nameLabel, 3);
        root.Children.Add(nameLabel);

        _worldName.Padding = new Thickness(8, 6, 8, 6);
        _worldName.MaxLength = 128;
        AutomationProperties.SetName(_worldName, "World name");
        Grid.SetRow(_worldName, 4);
        root.Children.Add(_worldName);

        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 18, 0, 0)
        };
        actions.Children.Add(new Button
        {
            Content = "Cancel",
            Padding = new Thickness(16, 6, 16, 6),
            Margin = new Thickness(0, 0, 8, 0),
            IsCancel = true
        });
        actions.Children.Add(_createButton);
        Grid.SetRow(actions, 5);
        root.Children.Add(actions);

        return root;
    }

    private void UpdateAction()
    {
        var name = _worldName.Text.Trim();
        _createButton.IsEnabled = _game.SelectedItem is CreateWorldOption &&
                                  name.Length is > 0 and <= 128 &&
                                  !name.Any(char.IsControl);
    }
}
