using System.ComponentModel;
using System.Windows;
using SharedWorlds.Core.Abstractions;
using SharedWorlds.Core.Worlds;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace SharedWorlds.Desktop;

public partial class MainWindow
{
    private readonly WorldLifecycleResponsibilityTracker _responsibilityTracker = new();
    private readonly IWorkspaceRecoveryStore _workspaceRecoveryStore;
    private Forms.NotifyIcon? _trayIcon;
    private bool _allowExplicitClose;
    private bool _quitRequestInProgress;
    private bool _quitRequestScheduledAfterClosing;
    private long _lifecyclePresentationEpoch;

    private IWorldLifecycleObserver CreateDesktopLifecycleObserver()
        => new DesktopLifecycleObserver(
            _responsibilityTracker,
            OnLifecyclePhaseChanged);

    private void InitializeTray()
    {
        _trayIcon = new Forms.NotifyIcon
        {
            Icon = ResolveSafeWorldTrayIcon(),
            Text = "Safe World",
            Visible = true,
            ContextMenuStrip = CreateTrayMenu()
        };
        _trayIcon.DoubleClick += (_, _) => OpenStewardWindow();

        Closing += MainWindow_Closing;
        Closed += (_, _) => DisposeTray();
        UpdateTrayStatus();
    }

    private static Drawing.Icon ResolveSafeWorldTrayIcon()
    {
        try
        {
            var processPath = Environment.ProcessPath;
            if (!string.IsNullOrWhiteSpace(processPath))
            {
                return Drawing.Icon.ExtractAssociatedIcon(processPath) ?? Drawing.SystemIcons.Application;
            }
        }
        catch (Exception) when (OperatingSystem.IsWindows())
        {
            // Tray branding is presentation-only. Failure to read the executable icon must never
            // block Safe World startup or change World lifecycle behavior.
        }

        return Drawing.SystemIcons.Application;
    }

    private Forms.ContextMenuStrip CreateTrayMenu()
    {
        var menu = new Forms.ContextMenuStrip
        {
            AccessibleName = "Safe World tray menu",
            AccessibleDescription = "Open or explicitly quit Safe World."
        };
        var open = new Forms.ToolStripMenuItem(DesktopText.OpenSteward)
        {
            AccessibleName = DesktopText.OpenSteward,
            AccessibleDescription = "Open the Safe World window."
        };
        open.Click += (_, _) => OpenStewardWindow();

        var quit = new Forms.ToolStripMenuItem(DesktopText.QuitSteward)
        {
            AccessibleName = DesktopText.QuitSteward,
            AccessibleDescription = "Quit Safe World. Unresolved World sessions require an explicit guarded decision."
        };
        quit.Click += (_, _) => RequestQuitSteward();

        menu.Items.Add(open);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(quit);
        return menu;
    }

    private async Task InitializeRuntimeResponsibilityAsync()
    {
        var records = await _workspaceRecoveryStore.ListAsync();
        _responsibilityTracker.InitializeFromRecoveryRecords(records);
        UpdateTrayStatus();
    }

    private void OnLifecyclePhaseChanged(WorldLifecyclePhaseChange change)
    {
        ArgumentNullException.ThrowIfNull(change);

        // Core responsibility is already updated synchronously by DesktopLifecycleObserver. This
        // monotonically increasing presentation epoch fences only delayed WPF work: if an older
        // callback is still queued when a newer lifecycle phase/session arrives, that old projection
        // must not unlock controls or overwrite the newer runtime presentation.
        var presentationEpoch = Interlocked.Increment(ref _lifecyclePresentationEpoch);
        if (!Dispatcher.CheckAccess())
        {
            _ = Dispatcher.BeginInvoke(new Action(
                () => RefreshRuntimePresentation(change, presentationEpoch)));
            return;
        }

        RefreshRuntimePresentation(change, presentationEpoch);
    }

    private void RefreshRuntimePresentation(
        WorldLifecyclePhaseChange? change = null,
        long? lifecyclePresentationEpoch = null)
    {
        if (lifecyclePresentationEpoch is { } epoch &&
            epoch != Volatile.Read(ref _lifecyclePresentationEpoch))
        {
            return;
        }

        if (change is not null)
        {
            ApplyHostedLifecycleShellState(change);
        }

        UpdateTrayStatus();
        UpdateUnifiedActionState();
        UpdateWorldSharingActionState();
        UpdateManagedHostStopUi();
        UpdateResponsibilityPresentation();
        RebuildManagedGameTiles();
    }

    private void ApplyHostedLifecycleShellState(WorldLifecyclePhaseChange change)
    {
        if (change.Mode != ManagedWorldSessionMode.Hosted)
        {
            return;
        }

        switch (change.Phase)
        {
            case WorldLifecyclePhase.Running:
                // The Host launch operation remains awaiting Factorio for the lifetime of the session.
                // Release that preparation-era foreground busy reason here, but keep lifecycle busy
                // independent so later capture/commit cannot be unlocked by another operation's finally.
                SetLifecycleBusy(false);
                SetBusy(false);
                break;

            case WorldLifecyclePhase.WaitingForSafeCapture:
            case WorldLifecyclePhase.Capturing:
            case WorldLifecyclePhase.StoringCandidate:
            case WorldLifecyclePhase.Committing:
            case WorldLifecyclePhase.Finalizing:
                SetLifecycleBusy(true);
                break;

            case WorldLifecyclePhase.Completed:
            case WorldLifecyclePhase.RecoveryNeeded:
            case WorldLifecyclePhase.CleanupPending:
                // Completion/recovery actions must become usable again. This clears only lifecycle
                // ownership; any independent foreground operation remains busy on its own.
                SetLifecycleBusy(false);
                break;
        }
    }

    private void UpdateTrayStatus()
    {
        if (_trayIcon is null)
        {
            return;
        }

        var snapshot = _responsibilityTracker.Current;
        _trayIcon.Text = snapshot.Kind switch
        {
            WorldLifecycleResponsibilityKind.None => "Safe World",
            WorldLifecycleResponsibilityKind.InterruptedSession => "Safe World - Interrupted session",
            WorldLifecycleResponsibilityKind.RecoveryNeeded => "Safe World - Recovery needed",
            WorldLifecycleResponsibilityKind.CleanupPending => "Safe World - Action required",
            _ => snapshot.Phase switch
            {
                WorldLifecyclePhase.Running => "Safe World - Running",
                WorldLifecyclePhase.WaitingForSafeCapture or
                WorldLifecyclePhase.Capturing or
                WorldLifecyclePhase.StoringCandidate or
                WorldLifecyclePhase.Committing or
                WorldLifecyclePhase.Finalizing => "Safe World - Saving World",
                _ => "Safe World - Preparing"
            }
        };
    }

    private void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        if (_allowExplicitClose)
        {
            return;
        }

        // X is an explicit quit request. Cancel this close attempt first and return control to WPF.
        // A modal dialog cannot safely use this window as Owner while WPF still considers it to be
        // inside the Closing transition. Dispatch the guarded decision only after that transition has
        // fully unwound; otherwise ShowDialog/Owner assignment throws and turns a normal Quit into an
        // unexpected fatal presentation failure.
        e.Cancel = true;
        if (_quitRequestScheduledAfterClosing)
        {
            return;
        }

        _quitRequestScheduledAfterClosing = true;
        _ = Dispatcher.BeginInvoke(new Action(() =>
        {
            _quitRequestScheduledAfterClosing = false;
            RequestQuitSteward();
        }));
    }

    private void OpenStewardWindow()
    {
        if (!IsVisible)
        {
            Show();
        }

        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        Activate();
    }

    private async void RequestQuitSteward()
    {
        if (_quitRequestInProgress)
        {
            return;
        }

        _quitRequestInProgress = true;
        try
        {
            var responsibility = _responsibilityTracker.Current;
            if (!responsibility.CanQuitWithoutGuard &&
                !await ConfirmGuardedQuitAsync(responsibility))
            {
                return;
            }

            CompleteExplicitQuit();
        }
        finally
        {
            _quitRequestInProgress = false;
        }
    }

    private void DisposeTray()
    {
        if (_trayIcon is null)
        {
            return;
        }

        _trayIcon.Visible = false;
        _trayIcon.Dispose();
        _trayIcon = null;
    }

    private sealed class DesktopLifecycleObserver : IWorldLifecycleObserver
    {
        private readonly WorldLifecycleResponsibilityTracker _tracker;
        private readonly Action<WorldLifecyclePhaseChange> _afterChange;

        public DesktopLifecycleObserver(
            WorldLifecycleResponsibilityTracker tracker,
            Action<WorldLifecyclePhaseChange> afterChange)
        {
            _tracker = tracker;
            _afterChange = afterChange;
        }

        public void OnPhaseChanged(WorldLifecyclePhaseChange change)
        {
            // The tracker is deterministic/in-memory responsibility state. Presentation updates are
            // explicitly best-effort: a tray/window projection failure must never alter capture,
            // commit, reservation release, or recovery behavior in Core.
            _tracker.OnPhaseChanged(change);
            try
            {
                _afterChange(change);
            }
            catch
            {
                // Presentation is non-authoritative. The next lifecycle/UI refresh may recover it.
            }
        }
    }
}