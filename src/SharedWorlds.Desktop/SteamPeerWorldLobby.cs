using System.Globalization;
using System.Windows.Threading;
using SharedWorlds.Core.Domain;
using SharedWorlds.Core.Errors;
using SharedWorlds.Infrastructure.Sessions;
using Steamworks;

namespace SharedWorlds.Desktop;

/// <summary>
/// Steam-backed ephemeral coordination for one active Steward World. Steam owns lobby membership and
/// transport-visible ownership; Steward metadata records which observed owner/generation has actually
/// been confirmed against persistent World authority. Durable World bytes never live in lobby data.
/// </summary>
internal sealed partial class SteamPeerWorldLobby : IPeerWorldLobby
{
    private const int MaxLobbyMembers = 250;
    private const string SchemaKey = "steward.schema";
    private const string SchemaVersion = "2";
    private const string WorldIdKey = "steward.world";
    private const string AuthorityOwnerKey = "steward.authority-owner";
    private const string AuthorityGenerationKey = "steward.authority-generation";
    private const string RequestedHostKey = "steward.requested-host";
    private const string CommittedRevisionKey = "steward.committed-revision";
    private const string RetiredKey = "steward.retired";
    private const string RetiredValue = "1";
    private const string ActiveValue = "0";
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
        ulong authorityGeneration,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(proposedOwner);
        EnsureLocalUser(proposedOwner);
        EnsurePositiveGeneration(authorityGeneration, worldId);

        await _mutationGate.WaitAsync(cancellationToken);
        try
        {
            var existing = await GetAsync(worldId, cancellationToken);
            if (existing is not null)
            {
                EnsureWritableOwner(existing, proposedOwner, authorityGeneration);
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
                            AuthorityGenerationKey,
                            GenerationText(authorityGeneration),
                            worldId);
                        EnsureSetLobbyData(lobbyId, RetiredKey, ActiveValue, worldId);
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
                        var created = ReadSnapshot(worldId, lobbyId);
                        EnsureWritableOwner(created, proposedOwner, authorityGeneration);
                        return created;
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
        ulong expectedAuthorityGeneration,
        UserIdentity requestedHost,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(expectedOwner);
        ArgumentNullException.ThrowIfNull(requestedHost);
        EnsureLocalUser(expectedOwner);
        EnsurePositiveGeneration(expectedAuthorityGeneration, worldId);
        var requestedSteamId = ParseSteamIdentity(requestedHost);

        await _mutationGate.WaitAsync(cancellationToken);
        try
        {
            return await InvokeSteamAsync(
                () =>
                {
                    var lobbyId = RequireKnownLobby(worldId);
                    var current = ReadSnapshot(worldId, lobbyId);
                    EnsureWritableOwner(
                        current,
                        expectedOwner,
                        expectedAuthorityGeneration);
                    if (current.RequestedHost is not null &&
                        !SameUser(current.RequestedHost, requestedHost))
                    {
                        throw new WorldSessionConflictException(
                            worldId,
                            "A different host handoff is already in progress.");
                    }

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
                    var updated = ReadSnapshot(worldId, lobbyId);
                    EnsureWritableOwner(
                        updated,
                        expectedOwner,
                        expectedAuthorityGeneration);
                    return updated;
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
        ulong expectedAuthorityGeneration,
        UserIdentity newOwner,
        ulong newAuthorityGeneration,
        RevisionId committedRevision,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(expectedOwner);
        ArgumentNullException.ThrowIfNull(newOwner);
        EnsureLocalUser(expectedOwner);
        EnsureGenerationTransition(
            expectedAuthorityGeneration,
            newAuthorityGeneration,
            worldId);
        var newOwnerSteamId = ParseSteamIdentity(newOwner);

        await _mutationGate.WaitAsync(cancellationToken);
        try
        {
            return await InvokeSteamAsync(
                () => TransferOwnershipCore(
                    worldId,
                    expectedOwner,
                    expectedAuthorityGeneration,
                    newOwner,
                    newOwnerSteamId,
                    newAuthorityGeneration,
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
        ulong expectedAuthorityGeneration,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(expectedOwner);
        EnsureLocalUser(expectedOwner);
        EnsurePositiveGeneration(expectedAuthorityGeneration, worldId);

        await _mutationGate.WaitAsync(cancellationToken);
        try
        {
            await InvokeSteamAsync(
                () =>
                {
                    var lobbyId = RequireKnownLobby(worldId);
                    var current = ReadSnapshot(worldId, lobbyId);
                    EnsureWritableOwner(
                        current,
                        expectedOwner,
                        expectedAuthorityGeneration);
                    if (current.RequestedHost is not null)
                    {
                        throw new WorldSessionConflictException(
                            worldId,
                            "The pending host handoff must resolve before leaving.");
                    }

                    // Normal Stop & Save ends this active session even if other Steward clients are
                    // still members of the Steam lobby. Steward first makes the lobby non-joinable and
                    // durably marks the lobby metadata retired while the confirmed host still owns it.
                    // Only then may the host leave. Steam is free to pick a platform lobby owner after
                    // that point, but the retired marker means the old lobby can never become a new
                    // Steward authority/session merely because Steam transferred ownership.
                    if (!SteamMatchmaking.SetLobbyJoinable(lobbyId, false))
                    {
                        throw new IOException(
                            $"Steam did not close new admissions for World '{worldId}' before session retirement.");
                    }

                    EnsureSetLobbyData(
                        lobbyId,
                        UpdatedAtKey,
                        TimestampText(DateTimeOffset.UtcNow),
                        worldId);
                    EnsureSetLobbyData(lobbyId, RetiredKey, RetiredValue, worldId);

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
    /// Schema, World identity, nonzero generation, and authority metadata are validated before the
    /// lobby becomes visible to peer coordination.
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
                    _ = ReadSnapshot(worldId, lobbyId);
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
        ulong expectedAuthorityGeneration,
        UserIdentity newOwner,
        CSteamID newOwnerSteamId,
        ulong newAuthorityGeneration,
        RevisionId committedRevision)
    {
        var lobbyId = RequireKnownLobby(worldId);
        var current = ReadSnapshot(worldId, lobbyId);
        EnsureWritableOwner(
            current,
            expectedOwner,
            expectedAuthorityGeneration);
        if (current.RequestedHost is null ||
            !SameUser(current.RequestedHost, newOwner))
        {
            throw new WorldSessionConflictException(
                worldId,
                "Steam lobby handoff target does not match Steward's requested host.");
        }

        EnsureLobbyMember(lobbyId, newOwnerSteamId, worldId);

        try
        {
            // The target has already ACKed durable generation N+1 before this method is called.
            // AuthorityOwner is deliberately the first mutation: once it succeeds the observed old
            // Steam owner is no longer confirmed. If even this first write fails, the catch below
            // immediately makes the source leave the live lobby so generation N cannot remain a
            // usable confirmed writer after N+1 is durable.
            EnsureSetLobbyData(
                lobbyId,
                AuthorityOwnerKey,
                SteamIdText(newOwnerSteamId),
                worldId);
            EnsureSetLobbyData(
                lobbyId,
                AuthorityGenerationKey,
                GenerationText(newAuthorityGeneration),
                worldId);
            EnsureSetLobbyData(
                lobbyId,
                CommittedRevisionKey,
                committedRevision.ToString(),
                worldId);
            EnsureSetLobbyData(lobbyId, RequestedHostKey, string.Empty, worldId);
            EnsureSetLobbyData(
                lobbyId,
                UpdatedAtKey,
                TimestampText(DateTimeOffset.UtcNow),
                worldId);

            var transferReportedSuccess = SteamMatchmaking.SetLobbyOwner(
                lobbyId,
                newOwnerSteamId);
            var observedOwner = SteamMatchmaking.GetLobbyOwner(lobbyId);
            var transitioned = ReadSnapshot(worldId, lobbyId, observedOwner);

            if (observedOwner == newOwnerSteamId)
            {
                if (!transitioned.OwnerConfirmed ||
                    transitioned.AuthorityGeneration != newAuthorityGeneration ||
                    transitioned.LastCommittedRevision != committedRevision)
                {
                    throw new InvalidDataException(
                        "Steam transferred lobby ownership without preserving Steward's generation-fenced handoff metadata.");
                }

                return transitioned;
            }

            throw new IOException(
                transferReportedSuccess
                    ? $"Steam reported host transfer success for World '{worldId}', but the generation-{newAuthorityGeneration} owner was not observable. The World is recovery-pending."
                    : $"Steam rejected host transfer for World '{worldId}' after generation {newAuthorityGeneration} was durably committed. The World is recovery-pending.");
        }
        catch
        {
            AbandonCommittedHandoffLobby(lobbyId, worldId);
            throw;
        }
    }

    private void AbandonCommittedHandoffLobby(CSteamID lobbyId, WorldId worldId)
    {
        // After the target ACKs N+1 the source has already relinquished durable authority. This is
        // not a normal LeaveAsync operation and therefore must not require the now-obsolete source
        // generation. Best-effort joinability shutdown reduces new admissions while Steam resolves
        // ownership; leaving ensures this installation cannot keep presenting itself as the live N
        // owner if metadata publication or SetLobbyOwner fails partway through.
        _ = SteamMatchmaking.SetLobbyJoinable(lobbyId, false);
        SteamMatchmaking.LeaveLobby(lobbyId);
        _knownLobbies.Remove(worldId);
    }

    private PeerWorldLobbySnapshot? TryReadKnownLobby(WorldId worldId)
    {
        if (!_knownLobbies.TryGetValue(worldId, out var lobbyId))
        {
            return null;
        }

        if (IsRetiredLobby(lobbyId))
        {
            // Retired lobbies are session tombstones, not recovery candidates. A surviving Steam
            // participant may temporarily become platform owner after the real Host leaves, but this
            // installation must detach instead of interpreting that platform transition as authority.
            SteamMatchmaking.LeaveLobby(lobbyId);
            _knownLobbies.Remove(worldId);
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
        if (observedOwner.m_SteamID == 0)
        {
            throw new WorldSessionConflictException(
                worldId,
                "The Steam lobby no longer has an observable owner.");
        }

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

        if (IsRetiredLobby(lobbyId))
        {
            throw new WorldSessionConflictException(
                worldId,
                "This Steam lobby belongs to a Steward session that has already ended.");
        }

        var authorityOwnerText = SteamMatchmaking.GetLobbyData(lobbyId, AuthorityOwnerKey);
        if (!TryParseSteamId(authorityOwnerText, out var authorityOwner))
        {
            throw new InvalidDataException(
                $"Steam lobby for World '{worldId}' has invalid Steward authority-owner metadata.");
        }

        var authorityGeneration = ParseAuthorityGeneration(
            SteamMatchmaking.GetLobbyData(lobbyId, AuthorityGenerationKey),
            worldId);
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
            AuthorityGeneration: authorityGeneration,
            RequestedHost: requestedHost,
            LastCommittedRevision: committedRevision,
            UpdatedAt: updatedAt);
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

    private void EnsureWritableOwner(
        PeerWorldLobbySnapshot snapshot,
        UserIdentity expectedOwner,
        ulong expectedAuthorityGeneration)
    {
        if (expectedAuthorityGeneration == 0 ||
            snapshot.AuthorityGeneration != expectedAuthorityGeneration ||
            !snapshot.OwnerConfirmed ||
            !SameUser(snapshot.Owner, expectedOwner))
        {
            throw new WorldSessionConflictException(
                snapshot.WorldId,
                "The local Steam user/generation is not the confirmed Steward authority for this World.");
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

    private static bool IsRetiredLobby(CSteamID lobbyId)
        => string.Equals(
            SteamMatchmaking.GetLobbyData(lobbyId, RetiredKey),
            RetiredValue,
            StringComparison.Ordinal);

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

    private static ulong ParseAuthorityGeneration(string value, WorldId worldId)
    {
        if (!ulong.TryParse(
                value,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var generation) ||
            generation == 0)
        {
            throw new InvalidDataException(
                $"Steam lobby has invalid authority-generation metadata for World '{worldId}'.");
        }

        return generation;
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

    private static void EnsurePositiveGeneration(ulong generation, WorldId worldId)
    {
        if (generation == 0)
        {
            throw new WorldSessionConflictException(
                worldId,
                "Peer authority generation must be nonzero.");
        }
    }

    private static void EnsureGenerationTransition(
        ulong expectedGeneration,
        ulong newGeneration,
        WorldId worldId)
    {
        EnsurePositiveGeneration(expectedGeneration, worldId);
        if (expectedGeneration == ulong.MaxValue ||
            newGeneration != expectedGeneration + 1)
        {
            throw new WorldSessionConflictException(
                worldId,
                $"Peer lobby authority must advance exactly one generation from {expectedGeneration}.");
        }
    }

    private static string SteamIdText(CSteamID steamId)
        => steamId.m_SteamID.ToString(CultureInfo.InvariantCulture);

    private static string GenerationText(ulong generation)
        => generation.ToString(CultureInfo.InvariantCulture);

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
