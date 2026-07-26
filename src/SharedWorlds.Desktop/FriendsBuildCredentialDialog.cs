using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;

namespace SharedWorlds.Desktop;

internal sealed class FriendsBuildCredentialDialog : Window
{
    private readonly PasswordBox _credential = new();
    private readonly Button _connectButton = new()
    {
        Content = "Connect",
        Padding = new Thickness(16, 6, 16, 6),
        IsDefault = true
    };

    public FriendsBuildCredentialDialog(string? message = null)
    {
        Title = "Connect to Steward";
        Width = 520;
        SizeToContent = SizeToContent.Height;
        MinWidth = 420;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        UseLayoutRounding = true;
        SnapsToDevicePixels = true;
        Background = (Brush)Application.Current.FindResource("AppBackgroundBrush");
        Foreground = (Brush)Application.Current.FindResource("TextBrush");
        Content = BuildContent(message);

        _credential.PasswordChanged += (_, _) => UpdateAction();
        _connectButton.Click += (_, _) =>
        {
            Credential = _credential.Password;
            DialogResult = true;
        };
        Loaded += (_, _) =>
        {
            _credential.Focus();
            UpdateAction();
        };
    }

    public string? Credential { get; private set; }

    private UIElement BuildContent(string? message)
    {
        var root = new Grid { Margin = new Thickness(20) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var heading = new StackPanel();
        heading.Children.Add(new TextBlock
        {
            Text = "Friends Build",
            FontSize = 22,
            FontWeight = FontWeights.SemiBold
        });
        heading.Children.Add(new TextBlock
        {
            Text = message ?? "Enter the private Steward Friends Build code you received.",
            Margin = new Thickness(0, 6, 0, 14),
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.78
        });
        Grid.SetRow(heading, 0);
        root.Children.Add(heading);

        var label = new TextBlock
        {
            Text = "Private code",
            Margin = new Thickness(0, 0, 0, 6)
        };
        Grid.SetRow(label, 1);
        root.Children.Add(label);

        _credential.Padding = new Thickness(8, 6, 8, 6);
        _credential.MaxLength = 256;
        AutomationProperties.SetName(_credential, "Private Friends Build code");
        Grid.SetRow(_credential, 2);
        root.Children.Add(_credential);

        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 18, 0, 0)
        };
        var cancelButton = new Button
        {
            Content = "Cancel",
            Padding = new Thickness(16, 6, 16, 6),
            Margin = new Thickness(0, 0, 8, 0),
            IsCancel = true
        };
        actions.Children.Add(cancelButton);
        actions.Children.Add(_connectButton);
        Grid.SetRow(actions, 3);
        root.Children.Add(actions);

        return root;
    }

    private void UpdateAction()
    {
        var value = _credential.Password;
        _connectButton.IsEnabled = !string.IsNullOrWhiteSpace(value) &&
                                   !value.Any(char.IsWhiteSpace) &&
                                   !value.Any(char.IsControl);
    }
}
