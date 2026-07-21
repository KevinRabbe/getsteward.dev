using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using SharedWorlds.Core.Worlds;

namespace SharedWorlds.Desktop;

public partial class MainWindow
{
    private Border? _worldResponsibilityBanner;
    private TextBlock? _worldResponsibilityText;
    private bool _responsibilityPresentationInitialized;
    private bool _applyingResponsibilityActionGuard;

    internal void InitializeResponsibilityPresentation()
    {
        if (_responsibilityPresentationInitialized)
        {
            return;
        }

        _responsibilityPresentationInitialized = true;

        _worldResponsibilityText = new TextBlock
        {
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap
        };
        _worldResponsibilityBanner = new Border
        {
            Visibility = Visibility.Collapsed,
            Margin = new Thickness(0, 14, 0, 0),
            Padding = new Thickness(12, 9, 12, 9),
            Background = (Brush)FindResource("PanelAltBrush"),
            BorderBrush = (Brush)FindResource("BorderBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Child = _worldResponsibilityText
        };

        // World name and game name stay first. Responsibility appears immediately after them,
        // before the ordinary sharing/version badges.
        WorldDetailsPanel.Children.Insert(2, _worldResponsibilityBanner);

        WorldList.SelectionChanged += (_, _) => UpdateResponsibilityPresentation();
        AllowHostingCheckBox.Click += (_, _) => UpdateResponsibilityPresentation();

        // Base action-state code may run for many unrelated reasons. These guards make it
        // impossible for it to re-enable a writable action while responsibility is unresolved.
        ContinueButton.IsEnabledChanged += (_, _) => EnforceResponsibilityActionGuard();
        HostButton.IsEnabledChanged += (_, _) => EnforceResponsibilityActionGuard();

        UpdateResponsibilityPresentation();
    }

    private void UpdateResponsibilityPresentation()
    {
        if (!_responsibilityPresentationInitialized ||
            _worldResponsibilityBanner is null ||
            _worldResponsibilityText is null)
        {
            return;
        }

        var snapshot = _responsibilityTracker.Current;
        var selectedWorld = _selectedWorld;

        if (snapshot.Kind == WorldLifecycleResponsibilityKind.None || selectedWorld is null)
        {
            _worldResponsibilityBanner.Visibility = Visibility.Collapsed;
            EnforceResponsibilityActionGuard();
            return;
        }

        var selectedOwnsResponsibility = snapshot.WorldId == selectedWorld.Id;
        _worldResponsibilityText.Text = selectedOwnsResponsibility
            ? FormatResponsibility(snapshot)
            : "Another World on this PC still has an active or unresolved Steward responsibility.";
        _worldResponsibilityBanner.Visibility = Visibility.Visible;

        EnforceResponsibilityActionGuard();
    }

    private void EnforceResponsibilityActionGuard()
    {
        if (_applyingResponsibilityActionGuard ||
            _responsibilityTracker.Current.Kind == WorldLifecycleResponsibilityKind.None)
        {
            return;
        }

        _applyingResponsibilityActionGuard = true;
        try
        {
            ContinueButton.IsEnabled = false;
            HostButton.IsEnabled = false;

            var snapshot = _responsibilityTracker.Current;
            var selectedOwnsResponsibility = _selectedWorld is not null &&
                                             snapshot.WorldId == _selectedWorld.Id;
            var message = selectedOwnsResponsibility
                ? "Resolve this World's active or recovery responsibility before starting another writable session."
                : "Another World on this PC has active or unresolved Steward responsibility.";
            ContinueButton.ToolTip = message;
            HostButton.ToolTip = message;
        }
        finally
        {
            _applyingResponsibilityActionGuard = false;
        }
    }

    private static string FormatResponsibility(WorldLifecycleResponsibilitySnapshot snapshot)
        => snapshot.Kind switch
        {
            WorldLifecycleResponsibilityKind.RecoveryNeeded => "Recovery needed",
            WorldLifecycleResponsibilityKind.CleanupPending => "Action required",
            WorldLifecycleResponsibilityKind.ActiveLifecycle => snapshot.Phase switch
            {
                WorldLifecyclePhase.Running => "Running",
                WorldLifecyclePhase.WaitingForSafeCapture or
                WorldLifecyclePhase.Capturing or
                WorldLifecyclePhase.StoringCandidate or
                WorldLifecyclePhase.Committing or
                WorldLifecyclePhase.Finalizing => "Saving World",
                _ => "Preparing"
            },
            _ => string.Empty
        };
}
