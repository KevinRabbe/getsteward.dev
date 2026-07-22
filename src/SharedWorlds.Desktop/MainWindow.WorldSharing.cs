using System.Windows;
using System.Windows.Threading;
using SharedWorlds.Core.Domain;
using SharedWorlds.Core.Worlds;

namespace SharedWorlds.Desktop;

public partial class MainWindow
{
    private bool _worldSharingUiInitialized;

    internal void InitializeWorldSharingUi()
    {
        if (_worldSharingUiInitialized)
        {
            return;
        }

        _worldSharingUiInitialized = true;

        // Replace the old honest placeholder only after the authenticated runtime had a chance to
        // initialize. UnifiedGames continues to own the common button location/layout; this partial
        // owns the real sharing transaction and its fail-closed state.
        ShareButton.Click -= UnifiedShareButton_Click;
        ShareButton.Click += ShareWorldButton_Click;

        // UnifiedGames still computes common action state after some list/busy events. Queue this
        // sharing-specific refinement at the end of the dispatcher turn so Share/Finish/Manage cannot
        // be overwritten by the older generic Shared/LocalOnly label immediately afterwards.
        WorldList.SelectionChanged += (_, _) => QueueWorldSharingActionStateUpdate();
        WorldList.IsEnabledChanged += (_, _) => QueueWorldSharingActionStateUpdate();

        UpdateWorldSharingActionState();
    }

    private async void ShareWorldButton_Click(object sender, RoutedEventArgs e)
    {
        var world = _selectedWorld;
        if (world is null)
        {
            return;
        }

        if (_remoteWorldIds.Contains(world.Id))
        {
            StatusText.Text =
                "This World is already shared. Access management is the next UI slice; canonical World authority is already remote.";
            return;
        }

        var remote = _remoteRuntime;
        if (remote is null)
        {
            StatusText.Text = world.SharingMode == WorldSharingMode.Shared
                ? "Sharing is incomplete. Reconnect authenticated Steward and use Finish sharing."
                : "Connect authenticated Steward before sharing this World.";
            return;
        }

        if (_responsibilityTracker.Current.Kind != WorldLifecycleResponsibilityKind.None)
        {
            StatusText.Text =
                "Resolve the active or recovery responsibility on this PC before publishing a World for sharing.";
            return;
        }

        await RunOperationAsync(
            world.SharingMode == WorldSharingMode.Shared
                ? $"Finishing sharing for {world.Name}..."
                : $"Sharing {world.Name}...",
            async () =>
            {
                World? localShadow = null;
                try
                {
                    localShadow = await _storage.LoadWorldAsync(world.Id)
                        ?? throw new InvalidOperationException(
                            "The selected World has no local canonical snapshot to publish.");
                    if (localShadow.SharingMode == WorldSharingMode.LocalOnly)
                    {
                        // Write-ahead authority intent: once any remote side effect can happen this
                        // local copy must never silently become a writable fallback. A crash after
                        // this write therefore resumes as Finish sharing instead of forking history.
                        localShadow = localShadow with { SharingMode = WorldSharingMode.Shared };
                        await _storage.SaveWorldAsync(localShadow);
                        _selectedWorld = localShadow;
                    }

                    var environmentId = localShadow.CurrentEnvironmentRevisionId
                        ?? throw new InvalidOperationException(
                            "The local World has no canonical environment revision to share.");
                    var stateId = localShadow.CurrentStateRevisionId
                        ?? throw new InvalidOperationException(
                            "The local World has no canonical state revision to share.");
                    var environment = await _storage.LoadEnvironmentRevisionAsync(
                        localShadow.Id,
                        environmentId)
                        ?? throw new InvalidOperationException(
                            "The local canonical environment revision is missing.");
                    var state = await _storage.LoadStateRevisionAsync(
                        localShadow.Id,
                        stateId)
                        ?? throw new InvalidOperationException(
                            "The local canonical state revision is missing.");

                    await using var package = await _storage.OpenRevisionAsync(
                        localShadow.Id,
                        stateId);
                    await remote.InitialWorldPublisher.PublishAsync(
                        localShadow,
                        environment,
                        state,
                        package,
                        remote.User);

                    await RefreshUnifiedWorldsAsync(localShadow.Id, preserveStatus: true);
                    StatusText.Text =
                        $"'{localShadow.Name}' is shared. Steward now treats the backend copy as its canonical authority.";
                }
                catch (Exception exception)
                {
                    // Never roll the write-ahead Shared marker back automatically. If backend World
                    // creation or immutable publication became ambiguous, local writable fallback is
                    // more dangerous than requiring an explicit retry of the same IDs.
                    if (localShadow?.SharingMode == WorldSharingMode.Shared ||
                        world.SharingMode == WorldSharingMode.Shared)
                    {
                        try
                        {
                            await RefreshUnifiedWorldsAsync(world.Id, preserveStatus: true);
                        }
                        catch
                        {
                            // The original failure remains the useful diagnostic; the durable local
                            // Shared marker already preserves the authority invariant.
                        }

                        throw new InvalidOperationException(
                            "Sharing did not finish. Steward kept this local World locked to remote authority so it cannot diverge. Reconnect and use Finish sharing to resume the same immutable publication.",
                            exception);
                    }

                    throw;
                }
            });

        UpdateWorldSharingActionState();
    }

    private void QueueWorldSharingActionStateUpdate()
    {
        if (!_worldSharingUiInitialized)
        {
            return;
        }

        _ = Dispatcher.BeginInvoke(
            DispatcherPriority.Background,
            new Action(UpdateWorldSharingActionState));
    }

    private void UpdateWorldSharingActionState()
    {
        if (!_worldSharingUiInitialized)
        {
            return;
        }

        var world = _selectedWorld;
        if (world is null)
        {
            ShareButton.Content = "Share World";
            ShareButton.IsEnabled = false;
            ShareButton.ToolTip = "Select a World.";
            return;
        }

        var unresolvedResponsibility =
            _responsibilityTracker.Current.Kind != WorldLifecycleResponsibilityKind.None;
        if (_remoteWorldIds.Contains(world.Id))
        {
            ShareButton.Content = "Manage access";
            ShareButton.IsEnabled = !_isBusy;
            ShareButton.ToolTip =
                "This World already uses Steward's remote canonical authority. Access-management UI is the next sharing slice.";
            return;
        }

        var finishing = world.SharingMode == WorldSharingMode.Shared ||
                        _remoteIncompleteWorldIds.Contains(world.Id);
        ShareButton.Content = finishing ? "Finish sharing" : "Share World";
        ShareButton.IsEnabled = !_isBusy &&
                                _remoteRuntime is not null &&
                                !unresolvedResponsibility;

        if (unresolvedResponsibility)
        {
            ShareButton.ToolTip =
                "Resolve this PC's active or recovery responsibility before publishing a World for sharing.";
        }
        else if (_remoteRuntime is null)
        {
            ShareButton.ToolTip = finishing
                ? "Reconnect authenticated Steward to resume this incomplete sharing transaction."
                : "Connect authenticated Steward before sharing this World.";
        }
        else if (finishing)
        {
            ShareButton.ToolTip =
                "Resume the same immutable World/environment/state publication. Local writable fallback remains locked until this finishes.";
        }
        else
        {
            ShareButton.ToolTip =
                "Publish this World's current immutable state and exact environment to Steward, then make remote authority canonical.";
        }
    }
}
