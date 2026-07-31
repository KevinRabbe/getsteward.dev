using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using SharedWorlds.Core.Worlds;

namespace SharedWorlds.Desktop;

internal sealed class GuardedQuitDialog : Window
{
    private readonly Button _quitButton;
    private bool _gameClosed;
    private bool _lossAcknowledged;

    public GuardedQuitDialog(
        string worldName,
        WorldLifecycleResponsibilitySnapshot responsibility,
        bool durableRecoveryExists)
    {
        Title = "Resolve or preserve this World session";
        Width = 560;
        SizeToContent = SizeToContent.Height;
        MinHeight = 360;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Background = (Brush)Application.Current.FindResource("AppBackgroundBrush");
        Foreground = (Brush)Application.Current.FindResource("TextBrush");
        _lossAcknowledged = durableRecoveryExists;

        var root = new Grid
        {
            Margin = new Thickness(28)
        };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var heading = new TextBlock
        {
            Text = "Safe World is still protecting this session",
            FontSize = 22,
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap
        };
        Grid.SetRow(heading, 0);
        root.Children.Add(heading);

        var state = DescribeResponsibility(responsibility);
        var explanation = new TextBlock
        {
            Text =
                $"'{worldName}' is still marked as {state}. Safe World normally stays open until the " +
                "session finishes, saves, or is resolved through its recovery actions.",
            Margin = new Thickness(0, 12, 0, 0),
            FontSize = 13,
            LineHeight = 20,
            TextWrapping = TextWrapping.Wrap
        };
        explanation.SetResourceReference(TextBlock.ForegroundProperty, "MutedTextBrush");
        Grid.SetRow(explanation, 1);
        root.Children.Add(explanation);

        var recoveryCard = new Border
        {
            Margin = new Thickness(0, 20, 0, 0),
            Padding = new Thickness(18),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12)
        };
        recoveryCard.SetResourceReference(Border.BackgroundProperty, "PanelBrush");
        recoveryCard.SetResourceReference(
            Border.BorderBrushProperty,
            durableRecoveryExists ? "BorderBrush" : "DangerBrush");

        var recoveryContent = new StackPanel();
        recoveryContent.Children.Add(new TextBlock
        {
            Text = durableRecoveryExists
                ? "Recovery evidence is safely stored"
                : "Recovery evidence could not be verified",
            FontSize = 15,
            FontWeight = FontWeights.SemiBold
        });
        var recoveryExplanation = new TextBlock
        {
            Text = durableRecoveryExists
                ? "After quitting, the next Safe World launch will show this as an interrupted session. The current canonical World is not overwritten or discarded."
                : "You can still force Safe World to close, but changes from this unresolved session may be lost. The last safely committed World remains unchanged.",
            Margin = new Thickness(0, 7, 0, 0),
            FontSize = 12,
            LineHeight = 19,
            TextWrapping = TextWrapping.Wrap
        };
        recoveryExplanation.SetResourceReference(TextBlock.ForegroundProperty, "MutedTextBrush");
        recoveryContent.Children.Add(recoveryExplanation);
        recoveryCard.Child = recoveryContent;
        Grid.SetRow(recoveryCard, 2);
        root.Children.Add(recoveryCard);

        var controls = new StackPanel
        {
            Margin = new Thickness(0, 22, 0, 0)
        };

        var gameClosed = new CheckBox
        {
            Content = new TextBlock
            {
                Text = "I have closed the game.",
                TextWrapping = TextWrapping.Wrap
            }
        };
        AutomationProperties.SetHelpText(
            gameClosed,
            "Confirm that the game process is closed before Safe World stops supervising the writable session.");
        gameClosed.Checked += (_, _) =>
        {
            _gameClosed = true;
            UpdateQuitButtonState();
        };
        gameClosed.Unchecked += (_, _) =>
        {
            _gameClosed = false;
            UpdateQuitButtonState();
        };
        controls.Children.Add(gameClosed);

        if (!durableRecoveryExists)
        {
            var acceptLoss = new CheckBox
            {
                Margin = new Thickness(0, 12, 0, 0),
                Content = new TextBlock
                {
                    Text = "I understand that changes from this unresolved session may be lost.",
                    TextWrapping = TextWrapping.Wrap
                }
            };
            AutomationProperties.SetHelpText(
                acceptLoss,
                "Acknowledge the additional risk when Safe World cannot verify a durable recovery record.");
            acceptLoss.Checked += (_, _) =>
            {
                _lossAcknowledged = true;
                UpdateQuitButtonState();
            };
            acceptLoss.Unchecked += (_, _) =>
            {
                _lossAcknowledged = false;
                UpdateQuitButtonState();
            };
            controls.Children.Add(acceptLoss);
        }

        var actions = new DockPanel
        {
            Margin = new Thickness(0, 22, 0, 0),
            LastChildFill = false
        };

        var returnButton = new Button
        {
            Content = "Return to World",
            MinWidth = 136,
            Height = 42,
            IsDefault = true,
            IsCancel = true
        };
        returnButton.Click += (_, _) => DialogResult = false;
        DockPanel.SetDock(returnButton, Dock.Right);
        actions.Children.Add(returnButton);

        _quitButton = new Button
        {
            Content = durableRecoveryExists
                ? "Quit and recover next time"
                : "Force quit without recovery",
            MinWidth = 190,
            Height = 42,
            Margin = new Thickness(0, 0, 10, 0),
            IsEnabled = false
        };
        if (Application.Current.TryFindResource("DangerButtonStyle") is Style dangerStyle)
        {
            _quitButton.Style = dangerStyle;
        }
        AutomationProperties.SetHelpText(
            _quitButton,
            durableRecoveryExists
                ? "Quit Safe World without deleting the durable recovery record. Resolve the interrupted session after the next launch."
                : "Force Safe World to close even though durable recovery evidence could not be verified.");
        _quitButton.Click += (_, _) => DialogResult = true;
        DockPanel.SetDock(_quitButton, Dock.Right);
        actions.Children.Add(_quitButton);

        controls.Children.Add(actions);
        Grid.SetRow(controls, 3);
        root.Children.Add(controls);

        Content = root;
    }

    private void UpdateQuitButtonState()
        => _quitButton.IsEnabled = _gameClosed && _lossAcknowledged;

    private static string DescribeResponsibility(WorldLifecycleResponsibilitySnapshot snapshot)
        => snapshot.Kind switch
        {
            WorldLifecycleResponsibilityKind.ActiveLifecycle => snapshot.Phase switch
            {
                WorldLifecyclePhase.Running => "running",
                WorldLifecyclePhase.WaitingForSafeCapture or
                WorldLifecyclePhase.Capturing or
                WorldLifecyclePhase.StoringCandidate or
                WorldLifecyclePhase.Committing or
                WorldLifecyclePhase.Finalizing => "saving",
                _ => "preparing"
            },
            WorldLifecycleResponsibilityKind.InterruptedSession => "interrupted",
            WorldLifecycleResponsibilityKind.RecoveryNeeded => "waiting for recovery",
            WorldLifecycleResponsibilityKind.CleanupPending => "waiting for cleanup",
            _ => "unresolved"
        };
}
