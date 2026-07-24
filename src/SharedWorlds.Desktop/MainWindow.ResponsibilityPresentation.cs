using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using SharedWorlds.Core.Worlds;

namespace SharedWorlds.Desktop;

public partial class MainWindow
{
    private Border? _worldResponsibilityBanner;
    private TextBlock? _worldResponsibilityText;
    private Button? _pendingSyncRetryButton;
    private Button? _recoverInterruptedButton;
    private Button? _discardInterruptedButton;
    private Button? _exportRecoveryCopyButton;
    private Button? _cleanupRetryButton;
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
        AutomationProperties.SetLiveSetting(_worldResponsibilityText, AutomationLiveSetting.Polite);
        RegisterLiveRegion(_worldResponsibilityText);

        _pendingSyncRetryButton = new Button
        {
            Content = DesktopText.RetryRecovery,
            Visibility = Visibility.Collapsed,
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 8, 0, 0)
        };
        SetResponsibilityActionHelp(
            _pendingSyncRetryButton,
            "Reconcile the journaled candidate with the canonical head. Steward never overwrites a different newer head automatically.");
        _pendingSyncRetryButton.Click += RetryPendingSyncButton_Click;

        _recoverInterruptedButton = new Button
        {
            Content = DesktopText.RetryRecovery,
            Visibility = Visibility.Collapsed,
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 8, 0, 0)
        };
        SetResponsibilityActionHelp(
            _recoverInterruptedButton,
            "Preserve the interrupted workspace, assign one stable candidate revision, and recover it only if the canonical head still matches its recorded base.");
        _recoverInterruptedButton.Click += RecoverInterruptedSessionButton_Click;

        _discardInterruptedButton = new Button
        {
            Content = DesktopText.ContinueFromLastSafeState,
            Visibility = Visibility.Collapsed,
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 8, 0, 0)
        };
        SetResponsibilityActionHelp(
            _discardInterruptedButton,
            "Explicitly keep the last canonical World unchanged and remove only the preserved interrupted workspace after confirmation.");
        _discardInterruptedButton.Click += DiscardInterruptedSessionButton_Click;

        _exportRecoveryCopyButton = new Button
        {
            Content = DesktopText.ExportRecoveryCopy,
            Visibility = Visibility.Collapsed,
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 8, 0, 0)
        };
        SetResponsibilityActionHelp(
            _exportRecoveryCopyButton,
            "Create a local ZIP copy of the preserved Steward workspace without changing the recovery journal or canonical World.");
        _exportRecoveryCopyButton.Click += ExportRecoveryCopyButton_Click;

        _cleanupRetryButton = new Button
        {
            Content = DesktopText.RetryCleanup,
            Visibility = Visibility.Collapsed,
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 8, 0, 0)
        };
        SetResponsibilityActionHelp(
            _cleanupRetryButton,
            "Retry adapter-owned workspace cleanup only. This does not capture, upload, commit, or change the canonical World head.");
        _cleanupRetryButton.Click += RetryCleanupButton_Click;

        var content = new StackPanel();
        content.Children.Add(_worldResponsibilityText);
        content.Children.Add(_pendingSyncRetryButton);
        content.Children.Add(_recoverInterruptedButton);
        content.Children.Add(_discardInterruptedButton);
        content.Children.Add(_exportRecoveryCopyButton);
        content.Children.Add(_cleanupRetryButton);

        _worldResponsibilityBanner = new Border
        {
            Visibility = Visibility.Collapsed,
            Margin = new Thickness(0, 14, 0, 0),
            Padding = new Thickness(12, 9, 12, 9),
            Background = (Brush)FindResource("PanelAltBrush"),
            BorderBrush = (Brush)FindResource("BorderBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Child = content
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
            _worldResponsibilityText is null ||
            _pendingSyncRetryButton is null ||
            _recoverInterruptedButton is null ||
            _discardInterruptedButton is null ||
            _exportRecoveryCopyButton is null ||
            _cleanupRetryButton is null)
        {
            return;
        }

        var snapshot = _responsibilityTracker.Current;
        var selectedWorld = _selectedWorld;

        if (snapshot.Kind == WorldLifecycleResponsibilityKind.None || selectedWorld is null)
        {
            _worldResponsibilityBanner.Visibility = Visibility.Collapsed;
            _pendingSyncRetryButton.Visibility = Visibility.Collapsed;
            _recoverInterruptedButton.Visibility = Visibility.Collapsed;
            _discardInterruptedButton.Visibility = Visibility.Collapsed;
            _exportRecoveryCopyButton.Visibility = Visibility.Collapsed;
            _cleanupRetryButton.Visibility = Visibility.Collapsed;
            EnforceResponsibilityActionGuard();
            return;
        }

        var selectedOwnsResponsibility = snapshot.WorldId == selectedWorld.Id;
        var hasAuthority = HasAuthoritativeRuntimeForWorld(selectedWorld);
        _worldResponsibilityText.Text = selectedOwnsResponsibility
            ? FormatResponsibility(snapshot)
            : "Another World on this PC still has an active or unresolved Steward responsibility.";

        _pendingSyncRetryButton.Visibility =
            selectedOwnsResponsibility &&
            snapshot.Kind == WorldLifecycleResponsibilityKind.RecoveryNeeded
                ? Visibility.Visible
                : Visibility.Collapsed;
        _pendingSyncRetryButton.IsEnabled = !_isBusy && hasAuthority;
        SetResponsibilityActionHelp(
            _pendingSyncRetryButton,
            hasAuthority
                ? "Reconcile the journaled candidate with the canonical head. Steward never overwrites a different newer head automatically."
                : "Reconnect authenticated Steward authority before retrying this shared recovery.");

        var interrupted = selectedOwnsResponsibility &&
                          snapshot.Kind == WorldLifecycleResponsibilityKind.InterruptedSession;
        _recoverInterruptedButton.Visibility = interrupted
            ? Visibility.Visible
            : Visibility.Collapsed;
        _discardInterruptedButton.Visibility = interrupted
            ? Visibility.Visible
            : Visibility.Collapsed;
        _recoverInterruptedButton.IsEnabled = !_isBusy && hasAuthority;
        _discardInterruptedButton.IsEnabled = !_isBusy && hasAuthority;
        SetResponsibilityActionHelp(
            _recoverInterruptedButton,
            hasAuthority
                ? "Preserve the interrupted workspace, assign one stable candidate revision, and recover it only if the canonical head still matches its recorded base."
                : "Reconnect authenticated Steward authority before resolving this interrupted shared World.");
        SetResponsibilityActionHelp(
            _discardInterruptedButton,
            hasAuthority
                ? "Explicitly continue from the last canonical safe state and remove only the preserved interrupted workspace after confirmation."
                : "Reconnect authenticated Steward authority before resolving this interrupted shared World.");

        var exportableRecovery = selectedOwnsResponsibility &&
                                 snapshot.Kind is (
                                     WorldLifecycleResponsibilityKind.InterruptedSession or
                                     WorldLifecycleResponsibilityKind.RecoveryNeeded or
                                     WorldLifecycleResponsibilityKind.CleanupPending);
        _exportRecoveryCopyButton.Visibility = exportableRecovery
            ? Visibility.Visible
            : Visibility.Collapsed;
        _exportRecoveryCopyButton.IsEnabled = !_isBusy;
        SetResponsibilityActionHelp(
            _exportRecoveryCopyButton,
            "Create a local ZIP copy of the preserved workspace. Export does not require backend authority and never changes the recovery journal or canonical World.");

        _cleanupRetryButton.Visibility =
            selectedOwnsResponsibility &&
            snapshot.Kind == WorldLifecycleResponsibilityKind.CleanupPending
                ? Visibility.Visible
                : Visibility.Collapsed;
        _cleanupRetryButton.IsEnabled = !_isBusy && hasAuthority;
        SetResponsibilityActionHelp(
            _cleanupRetryButton,
            hasAuthority
                ? "Retry adapter-owned workspace cleanup only. This does not capture, upload, commit, or change the canonical World head."
                : "Reconnect authenticated Steward authority before resolving cleanup for this shared World.");
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
            AutomationProperties.SetHelpText(ContinueButton, message);
            AutomationProperties.SetHelpText(HostButton, message);
        }
        finally
        {
            _applyingResponsibilityActionGuard = false;
        }
    }

    private static void SetResponsibilityActionHelp(Button button, string helpText)
    {
        button.ToolTip = helpText;
        AutomationProperties.SetHelpText(button, helpText);
    }

    private static string FormatResponsibility(WorldLifecycleResponsibilitySnapshot snapshot)
        => snapshot.Kind switch
        {
            WorldLifecycleResponsibilityKind.InterruptedSession =>
                "Interrupted session — choose Retry recovery or Continue from last safe state.",
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
