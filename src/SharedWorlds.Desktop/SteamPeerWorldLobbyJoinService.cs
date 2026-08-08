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
/// World identity, and confirmed host before it is attached to the peer coordinator.
/// </summary>
internal sealed class SteamPeerWorldLobbyJoinService : IDisposable
{
    private const string SchemaKey = "steward.schema";
    private const string SchemaVersion = "1";
    private const string WorldIdKey = "steward.world";
    private const string AuthorityOwnerKey = "steward.authority-owner";
    private const string RequestedHostKey = "steward.requested-host";
    private const uint SuccessfulLobbyEnterResponse = 1;
    private static readonly TimeSpan JoinTimeout = TimeSpan.FromSeconds(20);

    private readonly SteamPlatformRuntime _platform;
    private readonly SteamPeerWorldLobby _lobby;
    private readonly Callback<GameLobbyJoinRequested_t> _joinRequestedCallback;
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
    }

    private async Task<LobbyEnter_t> JoinSteamLobbyAsync(
        CSteamID lobbyId,
        CancellationToken cancellationToken)
    {
        var completion = new TaskCompletionSource<LobbyEnter_t>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var callResult = CallResult<LobbyEnter_t>.Create(
            (result, ioFailure) =>
            {
                if (ioFailure)
                {
                    completion.TrySetException(
                        new IOException("Steam lobby join failed because the Steam API call failed."));
                    return;
                }

                completion.TrySetResult(result);
            });

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
            return await completion.Task.WaitAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("Steam did not finish joining the Steward lobby in time.");
        }
    }

    private void OnGameLobbyJoinRequested(GameLobbyJoinRequested_t request)
    {
        if (_disposed || request.m_steamIDLobby.m_SteamID == 0)
        {
            return;
        }

        JoinRequested?.Invoke(request.m_steamIDLobby);
    }
}
