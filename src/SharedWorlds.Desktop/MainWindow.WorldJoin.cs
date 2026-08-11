using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Threading;
using SharedWorlds.Core.Abstractions;
using SharedWorlds.Core.Domain;
using SharedWorlds.Core.Worlds;
using SharedWorlds.Infrastructure.Remote;
using SharedWorlds.Infrastructure.Sessions;
using Steamworks;

namespace SharedWorlds.Desktop;

public partial class MainWindow
{
    private Button? _joinButton;
    private TextBlock? _joinReadinessText;
    private DispatcherTimer? _joinPresenceTimer;
    private StewardRemoteHostPresence? _selectedHostPresence;
    private Exception? _selectedHostPresenceError;
    private WorldId? _selectedHostPresenceWorldId;
    private PeerWorldLobbySnapshot? _selectedPeerLobby;
    private Exception? _selectedPeerLobbyError;
    private WorldId? _selectedPeerLobbyWorldId;
    private int _hostPresenceRefreshVersion;
    private bool _joinPresenceRefreshInProgress;
    private bool _peerInviteInProgress;

    internal async Task InitializeWorldJoinUiAsync()
    {
        if (_joinButton is not null)
        {
            return;
        }

        if (HostButton.Parent is not Panel playActions ||
            playActions.Parent is not Panel playSection)
        {
            throw new InvalidOperationException(
                "Steward could not attach Join to the common World play actions.");
        }

        var joinButton = new Button
        {
            Content = DesktopText.Join,
            Margin = new Thickness(0, 0, 10, 10),
            IsEnabled = false
        };
        AutomationProperties.SetName(joinButton, DesktopText.Join);

        var joinReadinessText = new TextBlock
        {
            Margin = new Thickness(0, 2, 0, 0),
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Visibility = Visibility.Collapsed
        };
        joinReadinessText.SetResourceReference(TextBlock.ForegroundProperty, "MutedTextBrush");
        AutomationProperties.SetName(joinReadinessText, "Join status");
        AutomationProperties.SetLiveSetting(joinReadinessText, AutomationLiveSetting.Polite);
        RegisterLiveRegion(joinReadinessText);

        var hostIndex = playActions.Children.IndexOf(HostButton);
        playActions.Children.Insert(hostIndex < 0 ? playActions.Children.Count : hostIndex + 1, joinButton);
        var playActionsIndex = playSection.Children.IndexOf(playActions);
        playSection.Children.Insert(
            playActionsIndex < 0 ? playSection.Children.Count : playActionsIndex + 1,
            joinReadinessText);
        _joinButton = joinButton;
        _joinReadinessText = joinReadinessText;

        SetJoinAvailability(joinButton, false, "Select a shared World to check Join readiness.");
        joinButton.Click += WorldJoinButton_Click;
        WorldList.SelectionChanged += async (_, _) => await RefreshSelectedWorldHostPresenceAsync();
        WorldList.IsEnabledChanged += (_, _) => UpdateWorldJoinActionState();

        if (_peerRuntime is { } peerRuntime)
        {
            peerRuntime.LobbyJoin.JoinRequested += PeerWorldLobbyJoinRequested;
        }

        _joinPresenceTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(10)
        };
        _joinPresenceTimer.Tick += async (_, _) => await RefreshSelectedWorldHostPresenceAsync();
        _joinPresenceTimer.Start();
        Closed += (_, _) => _joinPresenceTimer?.Stop();

        await RefreshSelectedWorldHostPresenceAsync();
    }

    private void PeerWorldLobbyJoinRequested(CSteamID lobbyId)
    {
        if (_peerInviteInProgress)
        {
            StatusText.Text =
                "Steward is already synchronizing a Steam World invitation. Finish that operation before accepting another.";
            return;
        }

        if (_isBusy)
        {
            StatusText.Text =
                "Steward is busy. Finish the current operation, then accept the Steam World invitation again.";
            return;
        }

        _ = HandlePeerWorldLobbyJoinRequestedAsync(lobbyId);
    }

    private async Task HandlePeerWorldLobbyJoinRequestedAsync(CSteamID lobbyId)
    {
        var runtime = _peerRuntime;
        if (runtime is null)
        {
            StatusText.Text =
                _peerRuntimeProblem is null
                    ? "Steam peer Join is unavailable on this launch."
                    : $"Steam peer Join is unavailable: {_peerRuntimeProblem}";
            return;
        }

        _peerInviteInProgress = true;
        try
        {
            await RunUnifiedOperationAsync(
                "Joining the private Steward Steam lobby and synchronizing its World...",
                async () =>
                {
                    var joined = await runtime.LobbyJoin.JoinAsync(lobbyId);
                    var snapshot = await RequirePeerJoinLobbyAsync(
                        runtime,
                        joined.WorldId);
                    if (!string.Equals(
                            snapshot.Owner.ExternalId,
                            joined.ConfirmedHostSteamId.m_SteamID.ToString(CultureInfo.InvariantCulture),
                            StringComparison.Ordinal))
                    {
                        throw new InvalidDataException(
                            "The attached Steam lobby changed host identity before Steward could synchronize the World.");
                    }

                    var localWorld = await runtime.Storage.LoadWorldAsync(joined.WorldId);
                    var result = await runtime.CatchUp.RequestCatchUpAsync(
                        snapshot.Owner,
                        new PeerWorldCatchUpRequest(
                            joined.WorldId,
                            snapshot.AuthorityGeneration,
                            localWorld?.CurrentStateRevisionId));
                    var synchronized = await RequireSynchronizedPeerWorldAsync(
                        runtime,
                        snapshot,
                        result);

                    await RefreshUnifiedWorldsAsync(
                        synchronized.Id,
                        preserveStatus: true);
                    StatusText.Text =
                        $"World '{synchronized.Name}' is synchronized from its active Steam host. Verify the exact environment, then use Join.";
                });
        }
        catch (Exception exception)
        {
            ShowError("Could not synchronize the invited Steam World", exception);
        }
        finally
        {
            _peerInviteInProgress = false;
            await RefreshSelectedWorldHostPresenceAsync();
        }
    }

    private async void WorldJoinButton_Click(object sender, RoutedEventArgs e)
    {
        var world = _selectedWorld;
        if (_isBusy || world is null)
        {
            return;
        }

        if (_peerWorldIds.Contains(world.Id))
        {
            if (!TryGetAdapter(world.GameAdapterId, out var peerAdapter))
            {
                return;
            }

            await JoinPeerWorldAsync(world, peerAdapter);
            return;
        }

        var runtime = _remoteRuntime;
        var displayedPresence = _selectedHostPresenceWorldId == world.Id
            ? _selectedHostPresence
            : null;
        if (runtime is null ||
            !_remoteWorldIds.Contains(world.Id) ||
            displayedPresence?.State != StewardRemoteHostPresenceState.Ready ||
            string.IsNullOrWhiteSpace(displayedPresence.Address) ||
            !TryGetAdapter(world.GameAdapterId, out var adapter))
        {
            return;
        }

        if (!adapter.Capabilities.HasFlag(GameAdapterCapabilities.AutomaticClientJoin))
        {
            StatusText.Text = $"{adapter.DisplayName} does not support Steward-managed Join yet.";
            return;
        }

        await RunUnifiedOperationAsync(
            $"Joining {world.Name}...",
            async () =>
            {
                // Local environment selection may take time. Re-read host presence only after that
                // work, immediately before launch, so the 10-second presentation cache can never be
                // treated as current multiplayer authority after a reservation reclaim.
                var installation = await GetReadyInstallationForWorldAsync(world, adapter);
                var presence = await runtime.GetHostPresenceAsync(world.Id);
                _selectedHostPresence = presence;
                _selectedHostPresenceError = null;
                _selectedHostPresenceWorldId = world.Id;

                if (presence?.State != StewardRemoteHostPresenceState.Ready ||
                    string.IsNullOrWhiteSpace(presence.Address))
                {
                    throw new InvalidOperationException(
                        "The host is no longer ready to join. Steward did not launch the client against stale host information.");
                }

                await runtime.Join.JoinAsync(
                    world.Id,
                    adapter,
                    installation,
                    new HostConnection(
                        presence.Address,
                        presence.Port,
                        presence.JoinToken));
                StatusText.Text = $"Left hosted World '{world.Name}'.";
            });

        await RefreshSelectedWorldHostPresenceAsync();
    }

    private async Task JoinPeerWorldAsync(
        World world,
        IGameAdapter adapter)
    {
        if (!adapter.Capabilities.HasFlag(GameAdapterCapabilities.AutomaticClientJoin))
        {
            StatusText.Text =
                $"{adapter.DisplayName} does not support Steward-managed peer Join yet.";
            return;
        }

        var runtime = RequirePeerRuntime(world);
        await RunUnifiedOperationAsync(
            $"Joining {world.Name} through its private Steam host...",
            async () =>
            {
                var snapshot = await RequirePeerJoinLobbyAsync(runtime, world.Id);
                var localWorld = await runtime.Storage.LoadWorldAsync(world.Id)
                    ?? throw new InvalidDataException(
                        "The local peer World disappeared before Join catch-up.");
                var catchUp = await runtime.CatchUp.RequestCatchUpAsync(
                    snapshot.Owner,
                    new PeerWorldCatchUpRequest(
                        world.Id,
                        snapshot.AuthorityGeneration,
                        localWorld.CurrentStateRevisionId));
                var synchronized = await RequireSynchronizedPeerWorldAsync(
                    runtime,
                    snapshot,
                    catchUp);

                var installation = await GetReadyInstallationForWorldAsync(
                    synchronized,
                    adapter);

                // Environment discovery/repair may take time. Re-read the attached lobby immediately
                // before opening the game bridge so stale generation/owner data never reaches launch.
                var launchSnapshot = await RequirePeerJoinLobbyAsync(
                    runtime,
                    synchronized.Id);
                if (launchSnapshot.AuthorityGeneration != catchUp.AuthorityGeneration ||
                    !SamePeerIdentity(launchSnapshot.Owner, snapshot.Owner))
                {
                    throw new InvalidOperationException(
                        "The World changed hosts while Steward prepared the local environment. Join again against the new host generation.");
                }

                await using var bridge = await runtime.GameBridge.OpenClientAsync(
                    synchronized.Id,
                    launchSnapshot.AuthorityGeneration,
                    launchSnapshot.Owner);
                var join = new WorldJoinService(runtime.Storage);
                await join.JoinAsync(
                    synchronized.Id,
                    adapter,
                    installation,
                    bridge.GameConnection);

                StatusText.Text = $"Left hosted World '{synchronized.Name}'.";
            });

        await RefreshSelectedWorldHostPresenceAsync();
    }

    private async Task<PeerWorldLobbySnapshot> RequirePeerJoinLobbyAsync(
        StewardDesktopPeerRuntime runtime,
        WorldId worldId)
    {
        var snapshot = await runtime.Lobby.GetAsync(worldId)
            ?? throw new InvalidOperationException(
                "Steward is not attached to this World's active private Steam lobby. Accept the host's Steam invitation first.");
        if (!snapshot.OwnerConfirmed ||
            snapshot.AuthorityGeneration == 0 ||
            snapshot.RequestedHost is not null)
        {
            throw new InvalidOperationException(
                "The private Steam lobby does not currently confirm a stable Steward host generation.");
        }

        if (SamePeerIdentity(snapshot.Owner, runtime.User))
        {
            throw new InvalidOperationException(
                "This Steward installation is the active host and does not Join itself through the peer bridge.");
        }

        return snapshot;
    }

    private static async Task<World> RequireSynchronizedPeerWorldAsync(
        StewardDesktopPeerRuntime runtime,
        PeerWorldLobbySnapshot snapshot,
        PeerWorldCatchUpResult result)
    {
        var world = await runtime.Storage.LoadWorldAsync(result.WorldId)
            ?? throw new InvalidDataException(
                "Peer catch-up completed without a local canonical World replica.");
        var authority = world.PeerAuthority
            ?? throw new InvalidDataException(
                "Peer catch-up completed without persistent World authority.");
        if (world.SharingMode != WorldSharingMode.Shared ||
            world.CurrentStateRevisionId != result.CurrentStateRevisionId ||
            authority.Generation != result.AuthorityGeneration ||
            snapshot.AuthorityGeneration != result.AuthorityGeneration ||
            !SamePeerIdentity(authority.Holder, snapshot.Owner) ||
            !world.Members.Any(member => SamePeerIdentity(member, runtime.User)))
        {
            throw new InvalidDataException(
                "Peer catch-up result does not match the local canonical World, live host generation, or membership.");
        }

        return world;
    }

    private async Task RefreshSelectedWorldHostPresenceAsync()
    {
        if (_joinButton is null || _joinPresenceRefreshInProgress)
        {
            return;
        }

        var world = _selectedWorld;
        var refreshVersion = ++_hostPresenceRefreshVersion;
        _selectedHostPresence = null;
        _selectedHostPresenceError = null;
        _selectedHostPresenceWorldId = world?.Id;
        _selectedPeerLobby = null;
        _selectedPeerLobbyError = null;
        _selectedPeerLobbyWorldId = world?.Id;
        UpdateWorldJoinActionState();

        if (world is null ||
            !TryGetAdapter(world.GameAdapterId, out var adapter) ||
            !SupportsJoinPresentation(adapter))
        {
            return;
        }

        if (_peerWorldIds.Contains(world.Id))
        {
            var peerRuntime = _peerRuntime;
            if (peerRuntime is null)
            {
                return;
            }

            _joinPresenceRefreshInProgress = true;
            try
            {
                PeerWorldLobbySnapshot? snapshot = null;
                Exception? failure = null;
                try
                {
                    snapshot = await peerRuntime.Lobby.GetAsync(world.Id);
                }
                catch (Exception exception) when (IsPeerJoinAvailabilityFailure(exception))
                {
                    failure = exception;
                }

                if (refreshVersion != _hostPresenceRefreshVersion ||
                    _selectedWorld?.Id != world.Id)
                {
                    return;
                }

                _selectedPeerLobby = snapshot;
                _selectedPeerLobbyError = failure;
                _selectedPeerLobbyWorldId = world.Id;
            }
            finally
            {
                _joinPresenceRefreshInProgress = false;
                UpdateWorldJoinActionState();
            }

            return;
        }

        var runtime = _remoteRuntime;
        if (runtime is null || !_remoteWorldIds.Contains(world.Id))
        {
            return;
        }

        _joinPresenceRefreshInProgress = true;
        try
        {
            StewardRemoteHostPresence? presence = null;
            Exception? failure = null;
            try
            {
                presence = await runtime.GetHostPresenceAsync(world.Id);
            }
            catch (Exception exception) when (IsRemoteAvailabilityFailure(exception))
            {
                failure = exception;
            }

            if (refreshVersion != _hostPresenceRefreshVersion || _selectedWorld?.Id != world.Id)
            {
                return;
            }

            _selectedHostPresence = presence;
            _selectedHostPresenceError = failure;
            _selectedHostPresenceWorldId = world.Id;
        }
        finally
        {
            _joinPresenceRefreshInProgress = false;
            UpdateWorldJoinActionState();
        }
    }

    private void UpdateWorldJoinActionState()
    {
        var button = _joinButton;
        if (button is null)
        {
            return;
        }

        var world = _selectedWorld;
        if (world is null)
        {
            SetJoinAvailability(button, false, "Select a shared World to check Join readiness.");
            return;
        }

        if (_peerWorldIds.Contains(world.Id))
        {
            UpdatePeerWorldJoinActionState(button, world);
            return;
        }

        if (!_remoteWorldIds.Contains(world.Id))
        {
            SetJoinAvailability(
                button,
                false,
                world.SharingMode == WorldSharingMode.Shared
                    ? "Reconnect Steward to check whether this shared World has a ready host."
                    : "Join is available for shared Worlds when another device is hosting.");
            return;
        }

        if (!TryGetAdapter(world.GameAdapterId, out var adapter) ||
            !SupportsJoinPresentation(adapter))
        {
            SetJoinAvailability(
                button,
                false,
                $"{adapter?.DisplayName ?? world.GameAdapterId} does not support one-click or native direct-connect Join yet.");
            return;
        }

        if (_remoteRuntime is null)
        {
            SetJoinAvailability(
                button,
                false,
                "Reconnect Steward to check whether a host is ready.");
            return;
        }

        if (!IsSelectedWorldEnvironmentReadyForPlay())
        {
            SetJoinAvailability(
                button,
                false,
                "Verify this World's exact environment before joining.");
            return;
        }

        if (_selectedHostPresenceError is not null)
        {
            SetJoinAvailability(button, false, "Steward cannot check host readiness right now.");
            return;
        }

        var presence = _selectedHostPresenceWorldId == world.Id
            ? _selectedHostPresence
            : null;
        if (presence is null)
        {
            SetJoinAvailability(button, false, "No one is hosting this World right now.");
            return;
        }

        if (presence.State == StewardRemoteHostPresenceState.Starting)
        {
            SetJoinAvailability(
                button,
                false,
                "A host is starting. Join will become available when it is ready.");
            return;
        }

        if (string.IsNullOrWhiteSpace(presence.Address))
        {
            SetJoinAvailability(button, false, "The host is not ready to accept connections yet.");
            return;
        }

        if (!adapter.Capabilities.HasFlag(GameAdapterCapabilities.AutomaticClientJoin) &&
            adapter is IManualDirectConnectProvider manualDirectConnect)
        {
            try
            {
                var guidance = manualDirectConnect.GetManualDirectConnectInstruction(
                    new HostConnection(
                        presence.Address,
                        presence.Port,
                        presence.JoinToken));
                SetJoinAvailability(button, false, guidance.Instruction);
            }
            catch (InvalidOperationException)
            {
                SetJoinAvailability(
                    button,
                    false,
                    "The host is ready, but it did not publish a complete native direct-connect endpoint.");
            }

            return;
        }

        SetJoinAvailability(
            button,
            !_isBusy,
            _isBusy
                ? "Another Steward operation is in progress."
                : "A host is ready. You can join now.");
    }

    private void UpdatePeerWorldJoinActionState(Button button, World world)
    {
        if (!TryGetAdapter(world.GameAdapterId, out var adapter) ||
            !adapter.Capabilities.HasFlag(GameAdapterCapabilities.AutomaticClientJoin))
        {
            SetJoinAvailability(
                button,
                false,
                $"{adapter?.DisplayName ?? world.GameAdapterId} does not support Steward-managed peer Join yet.");
            return;
        }

        var runtime = _peerRuntime;
        if (runtime is null)
        {
            SetJoinAvailability(
                button,
                false,
                _peerRuntimeProblem is null
                    ? "Steam peer Join is unavailable on this launch."
                    : $"Steam peer Join is unavailable: {_peerRuntimeProblem}");
            return;
        }

        if (!IsSelectedWorldEnvironmentReadyForPlay())
        {
            SetJoinAvailability(
                button,
                false,
                "Verify this World's exact environment before joining its Steam host.");
            return;
        }

        if (_selectedPeerLobbyError is not null)
        {
            SetJoinAvailability(
                button,
                false,
                "Steward cannot verify this World's private Steam lobby right now.");
            return;
        }

        var snapshot = _selectedPeerLobbyWorldId == world.Id
            ? _selectedPeerLobby
            : null;
        if (snapshot is null)
        {
            SetJoinAvailability(
                button,
                false,
                "Accept the active host's Steam invitation to attach this private World lobby before joining.");
            return;
        }

        if (!snapshot.OwnerConfirmed ||
            snapshot.AuthorityGeneration == 0 ||
            snapshot.RequestedHost is not null)
        {
            SetJoinAvailability(
                button,
                false,
                "The private Steam lobby is changing hosts or does not confirm a stable Steward generation yet.");
            return;
        }

        if (SamePeerIdentity(snapshot.Owner, runtime.User))
        {
            SetJoinAvailability(
                button,
                false,
                "This Steward installation is the active host for the selected World.");
            return;
        }

        SetJoinAvailability(
            button,
            !_isBusy,
            _isBusy
                ? "Another Steward operation is in progress."
                : "The private Steam host is active. Steward will verify the current World and encrypted game bridge when you join.");
    }

    private static bool SupportsJoinPresentation(IGameAdapter adapter)
    {
        ArgumentNullException.ThrowIfNull(adapter);
        return adapter.Capabilities.HasFlag(GameAdapterCapabilities.AutomaticClientJoin) ||
               adapter is IManualDirectConnectProvider;
    }

    private static bool SamePeerIdentity(UserIdentity left, UserIdentity right)
        => string.Equals(left.Provider, right.Provider, StringComparison.OrdinalIgnoreCase) &&
           string.Equals(left.ExternalId, right.ExternalId, StringComparison.Ordinal);

    private static bool IsPeerJoinAvailabilityFailure(Exception exception)
        => exception is IOException or
            InvalidDataException or
            InvalidOperationException or
            TimeoutException or
            TaskCanceledException;

    private void SetJoinAvailability(Button button, bool isEnabled, string helpText)
    {
        button.IsEnabled = isEnabled;
        button.ToolTip = helpText;
        AutomationProperties.SetHelpText(button, helpText);

        var status = _joinReadinessText;
        var world = _selectedWorld;
        if (status is null ||
            world is null ||
            world.SharingMode != WorldSharingMode.Shared)
        {
            if (status is not null)
            {
                status.Text = string.Empty;
                status.Visibility = Visibility.Collapsed;
            }

            return;
        }

        status.Text = helpText;
        status.Visibility = Visibility.Visible;
    }
}
