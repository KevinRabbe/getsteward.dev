using System.Windows;
using System.Windows.Automation;
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
        ShareButton.Click += ShareWorldButton_Click;

        // UnifiedGames updates the selected World first because its handlers were registered first.
        // WorldSharing then projects the one authoritative Share/Retry/Manage action from that state.
        WorldList.SelectionChanged += (_, _) => UpdateWorldSharingActionState();
        WorldList.IsEnabledChanged += (_, _) => UpdateWorldSharingActionState();

        UpdateWorldSharingActionState();
    }

    private async void ShareWorldButton_Click(object sender, RoutedEventArgs e)
    {
        var world = _selectedWorld;
        if (world is null)
        {
            return;
        }

        if (_peerWorldIds.Contains(world.Id))
        {
            var peer = _peerRuntime;
            if (peer is null)
            {
                StatusText.Text =
                    "This shared World uses persistent peer authority, but the embedded Steam runtime is unavailable.";
                return;
            }

            await RunOperationAsync(
                $"Loading access for {world.Name}...",
                async () =>
                {
                    var canonical = await peer.Storage.LoadWorldAsync(world.Id)
                        ?? throw new InvalidOperationException(
                            "The canonical peer World is no longer available on this Steward installation.");
                    var dialog = new PeerWorldAccessDialog(peer, canonical)
                    {
                        Owner = this
                    };
                    dialog.ShowDialog();

                    if (dialog.WorldWasLeft)
                    {
                        await RefreshUnifiedWorldsAsync(
                            preferredWorldId: null,
                            preserveStatus: true);
                        StatusText.Text = string.IsNullOrWhiteSpace(dialog.LeaveWarning)
                            ? $"You left shared World '{world.Name}'."
                            : dialog.LeaveWarning;
                        return;
                    }

                    await RefreshUnifiedWorldsAsync(
                        world.Id,
                        preserveStatus: true);
                    StatusText.Text = $"Access for '{world.Name}' is up to date.";
                });

            UpdateWorldSharingActionState();
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

        if (world.SharingMode == WorldSharingMode.LocalOnly)
        {
            var peerShareRuntime = _peerRuntime;
            if (peerShareRuntime is null)
            {
                StatusText.Text =
                    "Start Steward with its Steam peer runtime available before sharing a new World.";
                return;
            }

            if (_responsibilityTracker.Current.Kind != WorldLifecycleResponsibilityKind.None)
            {
                StatusText.Text =
                    "Resolve the active or recovery responsibility on this PC before sharing another World.";
                return;
            }

            await RunOperationAsync(
                $"Sharing {world.Name}...",
                async () =>
                {
                    var local = await peerShareRuntime.Storage.LoadWorldAsync(world.Id)
                        ?? throw new InvalidOperationException(
                            "The selected World has no local canonical snapshot to share.");
                    if (!TryGetAdapter(local.GameAdapterId, out var adapter))
                    {
                        throw new InvalidOperationException(
                            $"No installed Steward adapter can verify '{local.GameAdapterId}' before sharing.");
                    }

                    // Fresh peer sharing keeps the existing exact-environment preflight. No authority
                    // fence or Shared marker is written until this device proves it can reproduce the
                    // canonical environment for the selected World.
                    var selection = await SelectInstallationForWorldAsync(local, adapter);
                    RememberEnvironmentVerification(local, selection.Verification);
                    RememberVerifiedInstallation(local, selection.Installation);
                    UpdateEnvironmentReadinessUi();
                    if (!selection.Verification.IsReady)
                    {
                        throw new InvalidOperationException(
                            $"Steward will not share '{local.Name}' until this device can reproduce its exact canonical environment. Run Verify Environment and resolve the reported issue first.");
                    }

                    var shared = await peerShareRuntime.InitialShare.ShareAsync(
                        local.Id,
                        peerShareRuntime.User);
                    _selectedWorld = shared;
                    await RefreshUnifiedWorldsAsync(
                        shared.Id,
                        preserveStatus: true);
                    StatusText.Text =
                        $"'{shared.Name}' is shared through Steward's peer runtime. Add people from Manage access; private Steam invitations are delivered when a host is ready.";
                });

            UpdateWorldSharingActionState();
            return;
        }

        // From this point downward only pre-peer legacy Shared transactions are allowed. New LocalOnly
        // Worlds are never created in the centralized authority model after the peer sharing cutover.
        if (world.SharingMode != WorldSharingMode.Shared)
        {
            StatusText.Text = "This World has an unsupported sharing state.";
            return;
        }

        var remoteRuntime = _remoteRuntime;
        if (remoteRuntime is null)
        {
            StatusText.Text =
                "This older shared World still needs the legacy authenticated service to finish its existing sharing transaction.";
            return;
        }

        if (_responsibilityTracker.Current.Kind != WorldLifecycleResponsibilityKind.None)
        {
            StatusText.Text =
                "Resolve the active or recovery responsibility on this PC before retrying this legacy sharing transaction.";
            return;
        }

        await RunOperationAsync(
            $"Retrying sharing for {world.Name}...",
            async () =>
            {
                var localShadow = await _storage.LoadWorldAsync(world.Id)
                    ?? throw new InvalidOperationException(
                        "The selected legacy shared World has no local canonical snapshot to republish.");
                if (localShadow.SharingMode != WorldSharingMode.Shared ||
                    localShadow.PeerAuthority is not null)
                {
                    throw new InvalidOperationException(
                        "Legacy sharing retry accepts only a pre-peer Shared World. Persistent peer Worlds can never be published back to the old backend authority model.");
                }

                var environmentId = localShadow.CurrentEnvironmentRevisionId
                    ?? throw new InvalidOperationException(
                        "The local World has no canonical environment revision to republish.");
                var stateId = localShadow.CurrentStateRevisionId
                    ?? throw new InvalidOperationException(
                        "The local World has no canonical state revision to republish.");
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
                await remoteRuntime.InitialWorldPublisher.PublishAsync(
                    localShadow,
                    environment,
                    state,
                    package,
                    remoteRuntime.User);

                await RefreshUnifiedWorldsAsync(localShadow.Id, preserveStatus: true);
                StatusText.Text =
                    $"Legacy sharing transaction for '{localShadow.Name}' is up to date.";
            });

        UpdateWorldSharingActionState();
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
            SetShareActionState(DesktopText.ShareWorld, false, "Select a World.");
            return;
        }

        var unresolvedResponsibility =
            _responsibilityTracker.Current.Kind != WorldLifecycleResponsibilityKind.None;
        if (_peerWorldIds.Contains(world.Id))
        {
            var available = _peerRuntime is not null;
            SetShareActionState(
                DesktopText.ManageAccess,
                !_isBusy && available,
                available
                    ? "View canonical World members, add/remove access, or leave the World after another member holds active authority. Membership is canonical before Steam delivery/cleanup."
                    : "This World uses persistent peer authority, but the embedded Steam runtime is unavailable.");
            return;
        }

        if (_remoteWorldIds.Contains(world.Id))
        {
            SetShareActionState(
                DesktopText.ManageAccess,
                !_isBusy && _remoteRuntime is not null,
                "Invite players, remove access, transfer Access Manager responsibility, or leave this legacy shared World.");
            return;
        }

        if (world.SharingMode == WorldSharingMode.LocalOnly)
        {
            var available = _peerRuntime is not null;
            var isEnabled = !_isBusy && available && !unresolvedResponsibility;
            if (unresolvedResponsibility)
            {
                SetShareActionState(
                    DesktopText.ShareWorld,
                    false,
                    "Resolve this PC's active or recovery responsibility before sharing another World.");
            }
            else if (!available)
            {
                SetShareActionState(
                    DesktopText.ShareWorld,
                    false,
                    "Start Steward with its Steam peer runtime available before sharing a new World.");
            }
            else
            {
                SetShareActionState(
                    DesktopText.ShareWorld,
                    isEnabled,
                    "Verify the exact canonical environment, then establish durable generation-1 peer sharing on this device. No backend upload is required.");
            }

            return;
        }

        var legacyRetry = world.SharingMode == WorldSharingMode.Shared ||
                          _remoteIncompleteWorldIds.Contains(world.Id);
        if (!legacyRetry)
        {
            SetShareActionState(
                DesktopText.ShareWorld,
                false,
                "This World has an unsupported sharing state.");
            return;
        }

        var legacyAvailable = _remoteRuntime is not null;
        var legacyEnabled = !_isBusy && legacyAvailable && !unresolvedResponsibility;
        if (unresolvedResponsibility)
        {
            SetShareActionState(
                DesktopText.RetrySharing,
                false,
                "Resolve this PC's active or recovery responsibility before retrying the legacy sharing transaction.");
        }
        else if (!legacyAvailable)
        {
            SetShareActionState(
                DesktopText.RetrySharing,
                false,
                "Reconnect the legacy authenticated service to finish this pre-peer sharing transaction.");
        }
        else
        {
            SetShareActionState(
                DesktopText.RetrySharing,
                legacyEnabled,
                "Retry the existing immutable legacy publication. New Worlds are no longer created in this authority model.");
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
