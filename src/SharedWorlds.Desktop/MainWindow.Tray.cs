using System.ComponentModel;
using System.Windows;
using SharedWorlds.Core.Abstractions;
using SharedWorlds.Core.Worlds;
using Forms = System.Windows.Forms;

namespace SharedWorlds.Desktop;

public partial class MainWindow
{
    private readonly WorldLifecycleResponsibilityTracker _responsibilityTracker = new();
    private readonly IWorkspaceRecoveryStore _workspaceRecoveryStore;
    private Forms.NotifyIcon? _trayIcon;
    private bool _allowExplicitClose;

    private IWorldLifecycleObserver CreateDesktopLifecycleObserver()
        => new DesktopLifecycleObserver(
            _responsibilityTracker,
            OnLifecyclePhaseChanged);

    private void InitializeTray()
    {
        _trayIcon = new Forms.NotifyIcon
        {
            Icon = System.Drawing.SystemIcons.Application,
            Text = "Steward",
            Visible = true,
            ContextMenuStrip = CreateTrayMenu()
        };
        _trayIcon.DoubleClick += (_, _) => OpenStewardWindow();

        Closing += MainWindow_Closing;
        Closed += (_, _) => DisposeTray();
        UpdateTrayStatus();
    }

    private Forms.ContextMenuStrip CreateTrayMenu()
    {
        var menu = new Forms.ContextMenuStrip();
        var open = new Forms.ToolStripMenuItem("Open Steward");
        open.Click += (_, _) => OpenStewardWindow();

        var quit = new Forms.ToolStripMenuItem("Quit Steward");
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

        if (!Dispatcher.CheckAccess())
        {
            _ = Dispatcher.BeginInvoke(new Action(RefreshRuntimePresentation));
            return;
        }

        RefreshRuntimePresentation();
    }

    private void RefreshRuntimePresentation()
    {
        UpdateTrayStatus();
        UpdateUnifiedActionState();
        UpdateManagedHostStopUi();
        UpdateResponsibilityPresentation();
        RebuildManagedGameTiles();
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
            WorldLifecycleResponsibilityKind.None => "Steward",
            WorldLifecycleResponsibilityKind.RecoveryNeeded => "Steward - Recovery needed",
            WorldLifecycleResponsibilityKind.CleanupPending => "Steward - Action required",
            _ => snapshot.Phase switch
            {
                WorldLifecyclePhase.Running => "Steward - Running",
                WorldLifecyclePhase.WaitingForSafeCapture or
                WorldLifecyclePhase.Capturing or
                WorldLifecyclePhase.StoringCandidate or
                WorldLifecyclePhase.Committing or
                WorldLifecyclePhase.Finalizing => "Steward - Saving World",
                _ => "Steward - Preparing"
            }
        };
    }

    private void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        if (_allowExplicitClose)
        {
            return;
        }

        e.Cancel = true;
        Hide();
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

    private void RequestQuitSteward()
    {
        var responsibility = _responsibilityTracker.Current;
        if (!responsibility.CanQuitWithoutGuard)
        {
            MessageBox.Show(
                "Steward still has an active or unresolved World responsibility. Resolve or safely finish it before quitting.",
                "Steward is still responsible for a World",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        _allowExplicitClose = true;
        DisposeTray();
        Close();
        Application.Current.Shutdown();
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
