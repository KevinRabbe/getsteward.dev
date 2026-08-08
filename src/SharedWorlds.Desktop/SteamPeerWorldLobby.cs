using System.Globalization;
using System.Windows.Threading;
using SharedWorlds.Core.Domain;
using SharedWorlds.Core.Errors;
using SharedWorlds.Infrastructure.Sessions;
using Steamworks;

namespace SharedWorlds.Desktop;

/// <summary>
/// Steam-backed ephemeral coordination for one active Steward World. Steam owns lobby membership and
/// transport-visible ownership; Steward metadata records which observed owner has actually been
/// confirmed against World authority. Durable World bytes never live in lobby metadata.
/// </summary>
internal sealed class SteamPeerWorldLobby : IPeerWorldLobby
{
    private const int MaxLobbyMembers = 250;
    private const string SchemaKey = "steward.schema";
    private const string SchemaVersion = "1";
    private const string WorldIdKey = "steward.world";
    private const string AuthorityOwnerKey = "steward.authority-owner";
    private const string RequestedHostKey = "steward.requested-host";
    private const string CommittedRevisionKey = "steward.committed-revision";
    private const string UpdatedAtKey = "steward.updated-at";
    private static readonly TimeSpan SteamCallTimeout = TimeSpan.FromSeconds(20);

    private readonly SteamPlatformRuntime _platform;
    private readonly SemaphoreSlim _mutationGate = new(1, 1);
    private readonly Dictionary<WorldId, CSteamID> _knownLobbies = [];

    public SteamPeerWorldLobby(SteamPlatformRuntime platform)
    {
        ArgumentNullException.ThrowIfNull(platform);
        _platform = platform;
    }

    public async Task<PeerWorldLobbySnapshot?> GetAsync(
        WorldId worldId,
        CancellationToken cancellationToken = default)
        => await InvokeSteamAsync(
            () => TryReadKnownLobby(worldId),
            cancellationToken);

    public async Task<PeerWorldLobbySnapshot> CreateOrGetAsync(
        WorldId worldId,
        UserIdentity proposedOwner,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(proposedOwner);
        EnsureLocalUser(proposedOwner);

        await _mutationGate.WaitAsync(cancellationToken);
        try
        {
            var existing = await GetAsync(worldId, cancellationToken);
            if (existing is not null)
            {
                return existing;
            }

            var lobbyId = await CreateLobbyAsync(cancellationToken);
            try
            {
                return await InvokeSteamAsync(
                    () =>
                    {
                        EnsureObservedOwner(lobbyId, _platform.LocalSteamId, worldId);
                        EnsureSetLobbyData(lobbyId, SchemaKey, SchemaVersion, worldId);
                        EnsureSetLobbyData(lobbyId, WorldIdKey, worldId.ToString(), worldId);
                        EnsureSetLobbyData(
                            lobbyId,
                            AuthorityOwnerKey,
                            SteamIdText(_platform.LocalSteamId),
                            worldId);
                        EnsureSetLobbyData(
                            lobbyId,
                            UpdatedAtKey,
                            TimestampText(DateTimeOffset.UtcNow),
                            worldId);
                        if (!SteamMatchmaking.SetLobbyJoinable(lobbyId, true))
                        {
                            throw new IOException(
                                $"Steam did not make the lobby joinable for World '{worldId}'.");
                        }

                        _knownLobbies[worldId] = lobbyId;
                        return ReadSnapshot(worldId, lobbyId);
                    },
                    cancellationToken);
            }
            catch
            {
                await InvokeSteamAsync(
                    () => SteamMatchmaking.LeaveLobby(lobbyId),
                    CancellationToken.None);
                throw;
            }
        }
        finally
        {
            _mutationGate.Release();
        }
    }

    public async Task<PeerWorldLobbySnapshot> RequestHandoffAsync(
        WorldId worldId,
        UserIdentity expectedOwner,
        UserIdentity requestedHost,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(expectedOwner);
        ArgumentNullException.ThrowIfNull(requestedHost);
        EnsureLocalUser(expectedOwner);
        var requestedSteamId = ParseSteamIdentity(requestedHost);

        await _mutationGate.WaitAsync(cancellationToken);
        try
        {
            return await InvokeSteamAsync(
                () =>
                {
                    var lobbyId = RequireKnownLobby(worldId);
                    var current = ReadSnapshot(worldId, lobbyId);
                    EnsureWritableOwner(current, expectedOwner);
                    EnsureLobbyMember(lobbyId, requestedSteamId, worldId);

                    EnsureSetLobbyData(
                        lobbyId,
                        RequestedHostKey,
                        SteamIdText(requestedSteamId),
                        worldId);
                    EnsureSetLobbyData(
                        lobbyId,
                        UpdatedAtKey,
                        TimestampText(DateTimeOffset.UtcNow),
                        worldId);
                    return ReadSnapshot(worldId, lobbyId);
                },
                cancellationToken);
        }
        finally
        {
            _mutationGate.Release();
        }
    }

    public async Task<PeerWorldLobbySnapshot> TransferOwnershipAsync(
        WorldId worldId,
        UserIdentity expectedOwner,
        UserIdentity newOwner,
        RevisionId committedRevision,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(expectedOwner);
        ArgumentNullException.ThrowIfNull(newOwner);
        EnsureLocalUser(expectedOwner);
        var newOwnerSteamId = ParseSteamIdentity(newOwner);

        await _mutationGate.WaitAsync(cancellationToken);
        try
        {
            return await InvokeSteamAsync(
                () => TransferOwnershipCore(
                    worldId,
                    expectedOwner,
                    newOwner,
                    newOwnerSteamId,
                    committedRevision),
                cancellationToken);
        }
        finally
        {
            _mutationGate.Release();
        }
    }

    public async Task LeaveAsync(
        WorldId worldId,
        UserIdentity expectedOwner,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(expectedOwner);
        EnsureLocalUser(expectedOwner);

        await _mutationGate.WaitAsync(cancellationToken);
        try
        {
            await InvokeSteamAsync(
                () =>
                {
                    var lobbyId = RequireKnownLobby(worldId);
                    var current = ReadSnapshot(worldId, lobbyId);
                    EnsureWritableOwner(current, expectedOwner);

                    // A normal host exit with remaining participants must use Steward's explicit
                    // handoff path. Letting Steam choose a replacement here would create an
                    // unverified writer. Unexpected process/network loss is different: Steam may
                    // auto-select an owner, but AuthorityOwnerKey then deliberately disagrees and
                    // all surviving clients observe RecoveryPending.
                    if (SteamMatchmaking.GetNumLobbyMembers(lobbyId) > 1)
                    {
                        throw new WorldSessionConflictException(
                            worldId,
                            "Other participants are still connected. Steward must complete host handoff before the current host leaves.");
                    }

                    SteamMatchmaking.LeaveLobby(lobbyId);
                    _knownLobbies.Remove(worldId);
                },
                cancellationToken);
        }
        finally
        {
            _mutationGate.Release();
        }
    }

    /// <summary>
    /// Registers a lobby that this Steam client has already joined through the invite/join flow.
    /// The lobby must prove its Steward World identity before it becomes visible to the session
    /// coordinator. Joining itself is intentionally a separate operation from authority mutation.
    /// </summary>
    public async Task AttachJoinedLobbyAsync(
        WorldId worldId,
        CSteamID lobbyId,
        CancellationToken cancellationToken = default)
    {
        if (lobbyId.m_SteamID == 0)
        {
            throw new ArgumentException("A valid Steam lobby ID is required.", nameof(lobbyId));
        }

        await _mutationGate.WaitAsync(cancellationToken);
        try
        {
            await InvokeSteamAsync(
                () =>
                {
                    var snapshot = ReadSnapshot(worldId, lobbyId);
                    _ = snapshot;
                    _knownLobbies[worldId] = lobbyId;
                },
                cancellationToken);
        }
        finally
        {
            _mutationGate.Release();
        }
    }

    private PeerWorldLobbySnapshot TransferOwnershipCore(
        WorldId worldId,
        UserIdentity expectedOwner,
        UserIdentity newOwner,
        CSteamID newOwnerSteamId,
        RevisionId committedRevision)
    {
        var lobbyId = RequireKnownLobby(worldId);
        var current = ReadSnapshot(worldId, lobbyId);
        EnsureWritableOwner(current, expectedOwner);
        if (current.RequestedHost is null || !SameUser(current.RequestedHost, newOwner))
        {
            throw new WorldSessionConflictException(
                worldId,
                "Steam lobby handoff target does not match Steward's requested host.");
        }

        EnsureLobbyMember(lobbyId, newOwnerSteamId, worldId);

        var previousAuthority = SteamMatchmaking.GetLobbyData(lobbyId, AuthorityOwnerKey);
        var previousRequestedHost = SteamMatchmaking.GetLobbyData(lobbyId, RequestedHostKey);
        var previousCommittedRevision = SteamMatchmaking.GetLobbyData(lobbyId, CommittedRevisionKey);
        var previousUpdatedAt = SteamMatchmaking.GetLobbyData(lobbyId, UpdatedAtKey);

        // Publish the exact revision and intended authority before changing the Steam owner. Other
        // participants may briefly observe OwnerConfirmed=false during this transition, which is
        // intentionally safer than observing two confirmed writers.
        EnsureSetLobbyData(
            lobbyId,
            CommittedRevisionKey,
            committedRevision.ToString(),
            worldId);
        EnsureSetLobbyData(
            lobbyId,
            AuthorityOwnerKey,
            SteamIdText(newOwnerSteamId),
            worldId);
        EnsureSetLobbyData(lobbyId, RequestedHostKey, string.Empty, worldId);
        EnsureSetLobbyData(
            lobbyId,
            UpdatedAtKey,
            TimestampText(DateTimeOffset.UtcNow),
            worldId);

        var transferReportedSuccess = SteamMatchmaking.SetLobbyOwner(lobbyId, newOwnerSteamId);
        var observedOwner = SteamMatchmaking.GetLobbyOwner(lobbyId);
        if (observedOwner == newOwnerSteamId)
        {
            var transferred = ReadSnapshot(worldId, lobbyId);
            if (!transferred.OwnerConfirmed || transferred.LastCommittedRevision != committedRevision)
            {
                throw new InvalidDataException(
                    "Steam transferred lobby ownership without preserving Steward's committed handoff metadata.");
            }

            return transferred;
        }

        if (observedOwner == _platform.LocalSteamId)
        {
            RestoreLobbyData(
                lobbyId,
                worldId,
                previousAuthority,
                previousRequestedHost,
                previousCommittedRevision,
                previousUpdatedAt);
        }

        throw new IOException(
            transferReportedSuccess
                ? $"Steam reported host transfer success for World '{worldId}', but the new owner was not observable."
                : $"Steam rejected host transfer for World '{worldId}'.");
    }

    private void RestoreLobbyData(
        CSteamID lobbyId,
        WorldId worldId,
        string previousAuthority,
        string previousRequestedHost,
        string previousCommittedRevision,
        string previousUpdatedAt)
    {
        EnsureSetLobbyData(lobbyId, AuthorityOwnerKey, previousAuthority, worldId);
        EnsureSetLobbyData(lobbyId, RequestedHostKey, previousRequestedHost, worldId);
        EnsureSetLobbyData(lobbyId, CommittedRevisionKey, previousCommittedRevision, worldId);
        EnsureSetLobbyData(lobbyId, UpdatedAtKey, previousUpdatedAt, worldId);
    }

    private PeerWorldLobbySnapshot? TryReadKnownLobby(WorldId worldId)
    {
        if (!_knownLobbies.TryGetValue(worldId, out var lobbyId))
        {
            return null;
        }

        var owner = SteamMatchmaking.GetLobbyOwner(lobbyId);
        if (owner.m_SteamID == 0)
        {
            _knownLobbies.Remove(worldId);
            return null;
        }

        return ReadSnapshot(worldId, lobbyId, owner);
    }

    private PeerWorldLobbySnapshot ReadSnapshot(WorldId worldId, CSteamID lobbyId)
    {
        var owner = SteamMatchmaking.GetLobbyOwner(lobbyId);
        if (owner.m_SteamID == 0)
        {
            throw new WorldSessionConflictException(
                worldId,
                "The Steam lobby no longer has an observable owner.");
        }

        return ReadSnapshot(worldId, lobbyId, owner);
    }

    private PeerWorldLobbySnapshot ReadSnapshot(
        WorldId worldId,
        CSteamID lobbyId,
        CSteamID observedOwner)
    {
        var schema = SteamMatchmaking.GetLobbyData(lobbyId, SchemaKey);
        if (!string.Equals(schema, SchemaVersion, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Steam lobby for World '{worldId}' uses unsupported Steward schema '{schema}'.");
        }

        var storedWorldId = SteamMatchmaking.GetLobbyData(lobbyId, WorldIdKey);
        if (!string.Equals(storedWorldId, worldId.ToString(), StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"Steam lobby does not belong to expected Steward World '{worldId}'.");
        }

        var authorityOwnerText = SteamMatchmaking.GetLobbyData(lobbyId, AuthorityOwnerKey);
        if (!TryParseSteamId(authorityOwnerText, out var authorityOwner))
        {
            throw new InvalidDataException(
                $"Steam lobby for World '{worldId}' has invalid Steward authority metadata.");
        }

        var requestedHost = ParseOptionalUser(
            SteamMatchmaking.GetLobbyData(lobbyId, RequestedHostKey),
            worldId,
            RequestedHostKey);
        var committedRevision = ParseOptionalRevision(
            SteamMatchmaking.GetLobbyData(lobbyId, CommittedRevisionKey),
            worldId);
        var updatedAt = ParseUpdatedAt(
            SteamMatchmaking.GetLobbyData(lobbyId, UpdatedAtKey),
            worldId);

        return new PeerWorldLobbySnapshot(
            worldId,
            ToUser(observedOwner),
            OwnerConfirmed: observedOwner == authorityOwner,
            requestedHost,
            committedRevision,
            updatedAt);
    }

    private async Task<CSteamID> CreateLobbyAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var completion = new TaskCompletionSource<LobbyCreated_t>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var callResult = CallResult<LobbyCreated_t>.Create(
            (result, ioFailure) =>
            {
                if (ioFailure)
                {
                    completion.TrySetException(
                        new IOException("Steam lobby creation failed because the Steam API call failed."));
                    return;
                }

                completion.TrySetResult(result);
            });

        await InvokeSteamAsync(
            () =>
            {
                var call = SteamMatchmaking.CreateLobby(
                    ELobbyType.k_ELobbyTypeFriendsOnly,
                    MaxLobbyMembers);
                if (call == SteamAPICall_t.Invalid)
                {
                    throw new IOException("Steam rejected the Steward lobby creation request.");
                }

                callResult.Set(call);
            },
            cancellationToken);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(SteamCallTimeout);

        LobbyCreated_t created;
        try
        {
            created = await completion.Task.WaitAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("Steam did not finish creating the Steward lobby in time.");
        }

        if (created.m_eResult != EResult.k_EResultOK || created.m_ulSteamIDLobby == 0)
        {
            throw new IOException(
                $"Steam could not create the Steward lobby: {created.m_eResult}.");
        }

        return new CSteamID(created.m_ulSteamIDLobby);
    }

    private CSteamID RequireKnownLobby(WorldId worldId)
    {
        if (!_knownLobbies.TryGetValue(worldId, out var lobbyId))
        {
            throw new WorldSessionConflictException(
                worldId,
                "This Steward instance is not attached to the active Steam lobby for the World.");
        }

        return lobbyId;
    }

    private void EnsureWritableOwner(PeerWorldLobbySnapshot snapshot, UserIdentity expectedOwner)
    {
        if (!snapshot.OwnerConfirmed || !SameUser(snapshot.Owner, expectedOwner))
        {
            throw new WorldSessionConflictException(
                snapshot.WorldId,
                "The local Steam user is not the confirmed Steward host for this World.");
        }

        EnsureObservedOwner(
            RequireKnownLobby(snapshot.WorldId),
            _platform.LocalSteamId,
            snapshot.WorldId);
    }

    private static void EnsureObservedOwner(
        CSteamID lobbyId,
        CSteamID expectedOwner,
        WorldId worldId)
    {
        var observedOwner = SteamMatchmaking.GetLobbyOwner(lobbyId);
        if (observedOwner != expectedOwner)
        {
            throw new WorldSessionConflictException(
                worldId,
                "Steam lobby ownership changed before Steward could complete the operation.");
        }
    }

    private static void EnsureLobbyMember(
        CSteamID lobbyId,
        CSteamID expectedMember,
        WorldId worldId)
    {
        var count = SteamMatchmaking.GetNumLobbyMembers(lobbyId);
        for (var index = 0; index < count; index++)
        {
            if (SteamMatchmaking.GetLobbyMemberByIndex(lobbyId, index) == expectedMember)
            {
                return;
            }
        }

        throw new WorldSessionConflictException(
            worldId,
            "The requested next host is not currently joined to the Steam lobby.");
    }

    private static void EnsureSetLobbyData(
        CSteamID lobbyId,
        string key,
        string value,
        WorldId worldId)
    {
        if (!SteamMatchmaking.SetLobbyData(lobbyId, key, value))
        {
            throw new IOException(
                $"Steam rejected Steward lobby metadata '{key}' for World '{worldId}'.");
        }
    }

    private void EnsureLocalUser(UserIdentity user)
    {
        if (!SameUser(user, _platform.LocalUser))
        {
            throw new InvalidOperationException(
                "Steam lobby authority can be changed only for Steward's local Steam identity.");
        }
    }

    private static CSteamID ParseSteamIdentity(UserIdentity user)
    {
        if (!string.Equals(user.Provider, "steam", StringComparison.OrdinalIgnoreCase) ||
            !TryParseSteamId(user.ExternalId, out var steamId))
        {
            throw new ArgumentException(
                "A valid Steam UserIdentity is required for Steam lobby host coordination.",
                nameof(user));
        }

        return steamId;
    }

    private UserIdentity? ParseOptionalUser(string value, WorldId worldId, string key)
    {
        if (string.IsNullOrEmpty(value))
        {
            return null;
        }

        if (!TryParseSteamId(value, out var steamId))
        {
            throw new InvalidDataException(
                $"Steam lobby metadata '{key}' is invalid for World '{worldId}'.");
        }

        return ToUser(steamId);
    }

    private static RevisionId? ParseOptionalRevision(string value, WorldId worldId)
    {
        if (string.IsNullOrEmpty(value))
        {
            return null;
        }

        if (!Guid.TryParseExact(value, "N", out var parsed))
        {
            throw new InvalidDataException(
                $"Steam lobby has invalid committed revision metadata for World '{worldId}'.");
        }

        return new RevisionId(parsed);
    }

    private static DateTimeOffset ParseUpdatedAt(string value, WorldId worldId)
    {
        if (!long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var unixMilliseconds))
        {
            throw new InvalidDataException(
                $"Steam lobby has invalid update metadata for World '{worldId}'.");
        }

        try
        {
            return DateTimeOffset.FromUnixTimeMilliseconds(unixMilliseconds);
        }
        catch (ArgumentOutOfRangeException exception)
        {
            throw new InvalidDataException(
                $"Steam lobby update metadata is outside the supported range for World '{worldId}'.",
                exception);
        }
    }

    private UserIdentity ToUser(CSteamID steamId)
    {
        if (steamId == _platform.LocalSteamId)
        {
            return _platform.LocalUser;
        }

        var externalId = SteamIdText(steamId);
        return new UserIdentity("steam", externalId, externalId);
    }

    private static bool SameUser(UserIdentity left, UserIdentity right)
        => string.Equals(left.Provider, right.Provider, StringComparison.OrdinalIgnoreCase) &&
           string.Equals(left.ExternalId, right.ExternalId, StringComparison.Ordinal);

    private static bool TryParseSteamId(string value, out CSteamID steamId)
    {
        if (ulong.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) &&
            parsed != 0)
        {
            steamId = new CSteamID(parsed);
            return true;
        }

        steamId = CSteamID.Nil;
        return false;
    }

    private static string SteamIdText(CSteamID steamId)
        => steamId.m_SteamID.ToString(CultureInfo.InvariantCulture);

    private static string TimestampText(DateTimeOffset value)
        => value.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture);

    private async Task<T> InvokeSteamAsync<T>(
        Func<T> action,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(action);
        cancellationToken.ThrowIfCancellationRequested();
        if (_platform.Dispatcher.CheckAccess())
        {
            return action();
        }

        return await _platform.Dispatcher.InvokeAsync(
            action,
            DispatcherPriority.Normal,
            cancellationToken).Task;
    }

    private async Task InvokeSteamAsync(
        Action action,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(action);
        cancellationToken.ThrowIfCancellationRequested();
        if (_platform.Dispatcher.CheckAccess())
        {
            action();
            return;
        }

        await _platform.Dispatcher.InvokeAsync(
            action,
            DispatcherPriority.Normal,
            cancellationToken).Task;
    }
}
