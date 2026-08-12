using System.Globalization;
using SharedWorlds.Core.Domain;
using Steamworks;

namespace SharedWorlds.Desktop;

internal sealed record SteamJoinedWorldLobby(
    WorldId WorldId,
    CSteamID LobbyId,
    CSteamID ConfirmedHostSteamId);

/// <summary>
/// Handles the platform half of accepting a Steam World invitation. Joining a Steam lobby does not
/// itself grant writable World authority or launch a game. The lobby must prove its Steward protocol,
/// World identity, confirmed host, and nonzero authority generation before it is attached to the peer
/// coordinator.
/// </summary>
internal sealed class SteamPeerWorldLobbyJoinService : IDisposable
{
    private const string SchemaKey = "steward.schema";
    private const string SchemaVersion = "2";
    private const string WorldIdKey = "steward.world";
    private const string AuthorityOwnerKey = "steward.authority-owner";
    private const string AuthorityGenerationKey = "steward.authority-generation";
    private const string RequestedHostKey = "steward.requested-host";
    private const uint SuccessfulLobbyEnterResponse = 1;
    private static readonly TimeSpan JoinTimeout = TimeSpan.FromSeconds(20);

    private readonly SteamPlatformRuntime _platform;
    private readonly SteamPeerWorldLobby _lobby;
    private readonly Callback<GameLobbyJoinRequested_t> _joinRequestedCallback;
    private readonly object _lateJoinGate = new();
    private readonly HashSet<SteamLateCallGuard<LobbyEnter_t>> _lateJoinCalls = [];
    private bool _disposed;

    public SteamPeerWorldLobbyJoinService(
        SteamPlatformRuntime platform,
        SteamPeerWorldLobby lobby)
    {
        ArgumentNullException.ThrowIfNull(platform);
        ArgumentNullException.ThrowIfNull(lobby);
        if (!platform.Dispatcher.CheckAccess())
        {
            throw new InvalidOperationException(
                "Steam lobby join service must be created on Steward's Steam dispatcher.");
        }

        _platform = platform;
        _lobby = lobby;
        _joinRequestedCallback = Callback<GameLobbyJoinRequested_t>.Create(
            OnGameLobbyJoinRequested);
    }

    /// <summary>
    /// Raised only for Steam's explicit user-driven join request. The subscriber decides when to
    /// call JoinAsync; the callback itself never mutates Steward World state.
    /// </summary>
    public event Action<CSteamID>? JoinRequested;

    public async Task<SteamJoinedWorldLobby> JoinAsync(
        CSteamID lobbyId,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_platform.Dispatcher.CheckAccess())
        {
            throw new InvalidOperationException(
                "Steam lobby admission must run on Steward's Steam dispatcher.");
        }

        if (lobbyId.m_SteamID == 0)
        {
            throw new ArgumentException("A valid Steam lobby ID is required.", nameof(lobbyId));
        }

        var result = await JoinSteamLobbyAsync(lobbyId, cancellationToken);
        var joinedLobbyId = new CSteamID(result.m_ulSteamIDLobby);
        if (joinedLobbyId != lobbyId ||
            result.m_EChatRoomEnterResponse != SuccessfulLobbyEnterResponse)
        {
            if (joinedLobbyId.m_SteamID != 0)
            {
                SteamMatchmaking.LeaveLobby(joinedLobbyId);
            }

            throw new InvalidOperationException(
                $"Steam did not admit Steward to the requested lobby (response {result.m_EChatRoomEnterResponse}).");
        }

        try
        {
            var schema = SteamMatchmaking.GetLobbyData(lobbyId, SchemaKey);
            if (!string.Equals(schema, SchemaVersion, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"Steam lobby uses unsupported Steward schema '{schema}'.");
            }

            var worldText = SteamMatchmaking.GetLobbyData(lobbyId, WorldIdKey);
            if (!Guid.TryParseExact(worldText, "N", out var worldGuid))
            {
                throw new InvalidDataException(
                    "Steam lobby does not contain a valid Steward World ID.");
            }

            var worldId = new WorldId(worldGuid);
            var observedOwner = SteamMatchmaking.GetLobbyOwner(lobbyId);
            if (observedOwner.m_SteamID == 0)
            {
                throw new InvalidDataException(
                    $"Steam lobby for World '{worldId}' has no observable owner.");
            }

            var authorityText = SteamMatchmaking.GetLobbyData(lobbyId, AuthorityOwnerKey);
            if (!ulong.TryParse(
                    authorityText,
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out var authorityOwner) ||
                authorityOwner == 0 ||
                observedOwner.m_SteamID != authorityOwner)
            {
                throw new InvalidDataException(
                    $"Steam lobby for World '{worldId}' does not currently have confirmed Steward host authority.");
            }

            var generationText = SteamMatchmaking.GetLobbyData(
                lobbyId,
                AuthorityGenerationKey);
            if (!ulong.TryParse(
                    generationText,
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out var authorityGeneration) ||
                authorityGeneration == 0)
            {
                throw new InvalidDataException(
                    $"Steam lobby for World '{worldId}' does not contain a valid Steward authority generation.");
            }

            if (!string.IsNullOrEmpty(
                    SteamMatchmaking.GetLobbyData(lobbyId, RequestedHostKey)))
            {
                throw new InvalidOperationException(
                    $"World '{worldId}' is already changing hosts. Join again after that handoff resolves.");
            }

            await _lobby.AttachJoinedLobbyAsync(
                worldId,
                lobbyId,
                cancellationToken);
            return new SteamJoinedWorldLobby(
                worldId,
                lobbyId,
                observedOwner);
        }
        catch
        {
            SteamMatchmaking.LeaveLobby(lobbyId);
            throw;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _joinRequestedCallback.Dispose();
        JoinRequested = null;

        // Abandoned native calls deliberately stay registered until Steam reports their result so a
        // late successful Join can still be undone. SteamPlatformRuntime shuts the callback pump down
        // after its dependent services are disposed, which then clears any remaining platform state.
    }

    private async Task<LobbyEnter_t> JoinSteamLobbyAsync(
        CSteamID lobbyId,
        CancellationToken cancellationToken)
    {
        var attempt = new SteamLateCallGuard<LobbyEnter_t>(
            CleanupLateJoinResult,
            ReleaseLateJoinAttempt);
        var keepAliveForLateResult = false;
        var callResult = CallResult<LobbyEnter_t>.Create(
            (result, ioFailure) =>
            {
                if (ioFailure)
                {
                    attempt.Fail(
                        new IOException("Steam lobby join failed because the Steam API call failed."));
                    return;
                }

                attempt.Complete(result);
            });
        attempt.AttachRegistration(callResult);

        try
        {
            var call = SteamMatchmaking.JoinLobby(lobbyId);
            if (call == SteamAPICall_t.Invalid)
            {
                throw new IOException("Steam rejected the Steward lobby join request.");
            }

            callResult.Set(call);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(JoinTimeout);
            try
            {
                return await attempt.Completion.WaitAsync(timeout.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                keepAliveForLateResult = await AbandonJoinAttemptAsync(attempt);
                throw new TimeoutException(
                    "Steam did not finish joining the Steward lobby in time.");
            }
            catch (OperationCanceledException)
            {
                keepAliveForLateResult = await AbandonJoinAttemptAsync(attempt);
                throw;
            }
        }
        finally
        {
            if (!keepAliveForLateResult)
            {
                attempt.Dispose();
            }
        }
    }

    private async Task<bool> AbandonJoinAttemptAsync(
        SteamLateCallGuard<LobbyEnter_t> attempt)
    {
        RetainLateJoinAttempt(attempt);
        if (attempt.TryAbandon())
        {
            return true;
        }

        // The Steam callback won the cancellation/timeout race. Cleanup its already-completed result
        // synchronously with this operation before surfacing cancellation so no admitted lobby is lost.
        await attempt.CleanupCompletedResultAsync();
        return true;
    }

    private void CleanupLateJoinResult(LobbyEnter_t result)
    {
        var joinedLobbyId = new CSteamID(result.m_ulSteamIDLobby);
        if (joinedLobbyId.m_SteamID != 0)
        {
            SteamMatchmaking.LeaveLobby(joinedLobbyId);
        }
    }

    private void RetainLateJoinAttempt(SteamLateCallGuard<LobbyEnter_t> attempt)
    {
        lock (_lateJoinGate)
        {
            _lateJoinCalls.Add(attempt);
        }
    }

    private void ReleaseLateJoinAttempt(SteamLateCallGuard<LobbyEnter_t> attempt)
    {
        lock (_lateJoinGate)
        {
            _lateJoinCalls.Remove(attempt);
        }
    }

    private void OnGameLobbyJoinRequested(GameLobbyJoinRequested_t request)
    {
        if (_disposed || request.m_steamIDLobby.m_SteamID == 0)
        {
            return;
        }

        var subscribers = JoinRequested;
        if (subscribers is null)
        {
            return;
        }

        foreach (var subscriber in subscribers.GetInvocationList().Cast<Action<CSteamID>>())
        {
            try
            {
                subscriber(request.m_steamIDLobby);
            }
            catch
            {
                // A presentation subscriber must not break Steam's callback pump. Joining is still
                // explicit through JoinAsync, so ignoring a subscriber failure cannot mutate a World.
            }
        }
    }
}
