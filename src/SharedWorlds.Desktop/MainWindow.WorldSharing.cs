using System.Windows;
using System.Windows.Automation;
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
        // sharing-specific refinement at the end of the dispatcher turn so Share/Retry/Manage cannot
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
            var remote = _remoteRuntime;
            if (remote is null)
            {
                StatusText.Text = "Reconnect authenticated Steward before managing shared World access.";
                return;
            }

            await RunOperationAsync(
                $"Loading access for {world.Name}...",
                async () =>
                {
                    var metadata = await remote.GetWorldMetadataAsync(world.Id)
                        ?? throw new InvalidOperationException(
                            "The shared World is no longer accessible to this Steward account.");
                    var dialog = new WorldAccessDialog(
                        remote.Access,
                        world,
                        remote.User,
                        metadata.AccessManager)
                    {
                        Owner = this
                    };
                    dialog.ShowDialog();

                    await RefreshUnifiedWorldsAsync(
                        dialog.WorldLeft ? null : world.Id,
                        preserveStatus: true);
                    StatusText.Text = dialog.WorldLeft
                        ? $"You left shared World '{world.Name}'."
                        : $"Access for '{world.Name}' is up to date.";
                });

            UpdateWorldSharingActionState();
            return;
        }

        var remoteRuntime = _remoteRuntime;
        if (remoteRuntime is null)
        {
            StatusText.Text = world.SharingMode == WorldSharingMode.Shared
                ? "Sharing is incomplete. Reconnect authenticated Steward and use Retry sharing."
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
                ? $"Retrying sharing for {world.Name}..."
                : $"Sharing {world.Name}...",
            async () =>
            {
                World? localShadow = null;
                try
                {
                    localShadow = await _storage.LoadWorldAsync(world.Id)
                        ?? throw new InvalidOperationException(
                            "The selected World has no local canonical snapshot to publish.");

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

                    if (localShadow.SharingMode == WorldSharingMode.LocalOnly)
                    {
                        if (!TryGetAdapter(localShadow.GameAdapterId, out var adapter))
                        {
                            throw new InvalidOperationException(
                                $"No installed Steward adapter can verify '{localShadow.GameAdapterId}' before sharing.");
                        }

                        // Initial sharing must prove that the canonical environment is reproducible before
                        // any remote side effect or local authority lock is written. Evaluate every discovered
                        // installation so Steam library ordering can never decide which environment is shared.
                        var selection = await SelectInstallationForWorldAsync(localShadow, adapter);
                        RememberEnvironmentVerification(localShadow, selection.Verification);
                        RememberVerifiedInstallation(localShadow, selection.Installation);
                        UpdateEnvironmentReadinessUi();
                        if (!selection.Verification.IsReady)
                        {
                            throw new InvalidOperationException(
                                $"Steward will not share '{localShadow.Name}' until this device can reproduce its exact canonical environment. Run Verify Environment and resolve the reported issue first.");
                        }

                        // Write-ahead authority intent: once any remote side effect can happen this
                        // local copy must never silently become a writable fallback. A crash after
                        // this write therefore resumes as Retry sharing instead of forking history.
                        localShadow = localShadow with { SharingMode = WorldSharingMode.Shared };
                        await _storage.SaveWorldAsync(localShadow);
                        _selectedWorld = localShadow;
                    }

                    await using var package = await _storage.OpenRevisionAsync(
                        localShadow.Id,
                        stateId);
                    await remoteRuntime.InitialWorldPublisher.PublishAsync(
                        localShadow,
                        environment,
                        state,
                        package,
                        remoteRuntime.User);

                    await RefreshUnifiedWorldsAsync(localShadow.Id, preserveStatus: true);
                    StatusText.Text =
                        $"'{localShadow.Name}' is shared. Steward now treats the backend copy as its canonical authority.";
                }
                catch (Exception exception)
                {
                    // Never roll the write-ahead Shared marker back automatically. If backend World
                    // creation or immutable publication became ambiguous, local writable fallback is
                    // more dangerous than requiring an explicit retry of the same IDs. Failures before
                    // that marker (including exact-environment preflight) remain ordinary LocalOnly failures.
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
                            "Sharing did not finish. Steward kept this local World locked to remote authority so it cannot diverge. Reconnect and use Retry sharing to resume the same immutable publication.",
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
            SetShareActionState("Share World", false, "Select a World.");
            return;
        }

        var unresolvedResponsibility =
            _responsibilityTracker.Current.Kind != WorldLifecycleResponsibilityKind.None;
        if (_remoteWorldIds.Contains(world.Id))
        {
            SetShareActionState(
                "Manage access",
                !_isBusy && _remoteRuntime is not null,
                "Invite players, remove access, transfer Access Manager responsibility, or leave this shared World.");
            return;
        }

        var retrying = world.SharingMode == WorldSharingMode.Shared ||
                       _remoteIncompleteWorldIds.Contains(world.Id);
        var content = retrying ? "Retry sharing" : "Share World";
        var isEnabled = !_isBusy &&
                        _remoteRuntime is not null &&
                        !unresolvedResponsibility;

        if (unresolvedResponsibility)
        {
            SetShareActionState(
                content,
                isEnabled,
                "Resolve this PC's active or recovery responsibility before publishing a World for sharing.");
        }
        else if (_remoteRuntime is null)
        {
            SetShareActionState(
                content,
                isEnabled,
                retrying
                    ? "Reconnect authenticated Steward to retry this incomplete sharing transaction."
                    : "Connect authenticated Steward before sharing this World.");
        }
        else if (retrying)
        {
            SetShareActionState(
                content,
                isEnabled,
                "Retry the same immutable World/environment/state publication. Local writable fallback remains locked until this finishes.");
        }
        else
        {
            SetShareActionState(
                content,
                isEnabled,
                "Verify the exact canonical environment, then publish this World's immutable state to Steward and make remote authority canonical.");
        }
    }

    private void SetShareActionState(string content, bool isEnabled, string helpText)
    {
        ShareButton.Content = content;
        ShareButton.IsEnabled = isEnabled;
        ShareButton.ToolTip = helpText;
        AutomationProperties.SetHelpText(ShareButton, helpText);
    }
}
