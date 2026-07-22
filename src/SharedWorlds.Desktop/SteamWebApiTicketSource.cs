using System.Globalization;
using System.IO;
using Steamworks;
using SharedWorlds.Core.Domain;

namespace SharedWorlds.Desktop;

internal sealed class SteamWebApiTicketSource : IDisposable
{
    private static readonly TimeSpan TicketTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan CallbackPumpInterval = TimeSpan.FromMilliseconds(25);

    private readonly SemaphoreSlim _requestGate = new(1, 1);
    private readonly Callback<GetTicketForWebApiResponse_t> _ticketCallback;
    private TaskCompletionSource<GetTicketForWebApiResponse_t>? _pendingCompletion;
    private HAuthTicket _pendingHandle = HAuthTicket.Invalid;
    private bool _disposed;

    private SteamWebApiTicketSource()
    {
        _ticketCallback = Callback<GetTicketForWebApiResponse_t>.Create(OnTicketResponse);
    }

    public static bool TryCreate(
        uint expectedAppId,
        out SteamWebApiTicketSource? source,
        out string? problem)
    {
        if (expectedAppId == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(expectedAppId));
        }

        bool initialized;
        try
        {
            initialized = SteamAPI.Init();
        }
        catch (Exception exception) when (
            exception is DllNotFoundException or
            EntryPointNotFoundException or
            BadImageFormatException or
            TypeInitializationException)
        {
            source = null;
            problem = $"Steamworks could not be loaded: {exception.Message}";
            return false;
        }

        if (!initialized)
        {
            source = null;
            problem =
                "Steam could not initialize. Start Steward through the configured Steam app with the Steam client running.";
            return false;
        }

        try
        {
            var actualAppId = SteamUtils.GetAppID().m_AppId;
            if (actualAppId != expectedAppId)
            {
                SteamAPI.Shutdown();
                source = null;
                problem =
                    $"Steam initialized AppID {actualAppId}, but Steward is configured for AppID {expectedAppId}.";
                return false;
            }

            source = new SteamWebApiTicketSource();
            problem = null;
            return true;
        }
        catch
        {
            SteamAPI.Shutdown();
            throw;
        }
    }

    public async Task<SteamWebApiTicketLease> RequestAsync(
        string identity,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(identity);
        ThrowIfDisposed();

        await _requestGate.WaitAsync(cancellationToken);
        HAuthTicket issuedHandle = HAuthTicket.Invalid;
        try
        {
            ThrowIfDisposed();
            var completion = new TaskCompletionSource<GetTicketForWebApiResponse_t>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            _pendingCompletion = completion;

            issuedHandle = SteamUser.GetAuthTicketForWebApi(identity);
            if (issuedHandle == HAuthTicket.Invalid)
            {
                throw new InvalidOperationException("Steam rejected the Web API authentication ticket request.");
            }

            _pendingHandle = issuedHandle;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TicketTimeout);

            try
            {
                while (!completion.Task.IsCompleted)
                {
                    timeout.Token.ThrowIfCancellationRequested();
                    SteamAPI.RunCallbacks();
                    await Task.Delay(CallbackPumpInterval, timeout.Token);
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException("Steam did not return a Web API authentication ticket in time.");
            }

            var response = await completion.Task;
            if (response.m_eResult != EResult.k_EResultOK)
            {
                throw new InvalidOperationException(
                    $"Steam returned '{response.m_eResult}' while creating the Web API authentication ticket.");
            }

            if (response.m_rgubTicket is null ||
                response.m_cubTicket <= 0 ||
                response.m_cubTicket > response.m_rgubTicket.Length)
            {
                throw new InvalidDataException("Steam returned an invalid Web API authentication ticket payload.");
            }

            var steamId = SteamUser.GetSteamID().m_SteamID;
            if (steamId == 0)
            {
                throw new InvalidDataException("Steam returned an invalid local SteamID64.");
            }

            var externalId = steamId.ToString(CultureInfo.InvariantCulture);
            var displayName = SteamFriends.GetPersonaName();
            var user = new UserIdentity(
                "steam",
                externalId,
                string.IsNullOrWhiteSpace(displayName) ? externalId : displayName);
            var ticketHex = Convert.ToHexString(
                response.m_rgubTicket.AsSpan(0, response.m_cubTicket));

            var lease = new SteamWebApiTicketLease(issuedHandle, ticketHex, user);
            issuedHandle = HAuthTicket.Invalid;
            return lease;
        }
        catch
        {
            if (issuedHandle != HAuthTicket.Invalid)
            {
                SteamUser.CancelAuthTicket(issuedHandle);
            }

            throw;
        }
        finally
        {
            _pendingCompletion = null;
            _pendingHandle = HAuthTicket.Invalid;
            _requestGate.Release();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _pendingCompletion?.TrySetException(
            new ObjectDisposedException(nameof(SteamWebApiTicketSource)));
        _ticketCallback.Dispose();
        _requestGate.Dispose();
        SteamAPI.Shutdown();
    }

    private void OnTicketResponse(GetTicketForWebApiResponse_t response)
    {
        if (_pendingCompletion is null || response.m_hAuthTicket != _pendingHandle)
        {
            return;
        }

        _pendingCompletion.TrySetResult(response);
    }

    private void ThrowIfDisposed()
        => ObjectDisposedException.ThrowIf(_disposed, this);
}

internal sealed class SteamWebApiTicketLease : IDisposable
{
    private HAuthTicket _handle;

    public SteamWebApiTicketLease(
        HAuthTicket handle,
        string ticketHex,
        UserIdentity user)
    {
        if (handle == HAuthTicket.Invalid)
        {
            throw new ArgumentException("A valid Steam auth ticket handle is required.", nameof(handle));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(ticketHex);
        ArgumentNullException.ThrowIfNull(user);
        _handle = handle;
        TicketHex = ticketHex;
        User = user;
    }

    public string TicketHex { get; }
    public UserIdentity User { get; }

    public void Dispose()
    {
        var handle = _handle;
        if (handle == HAuthTicket.Invalid)
        {
            return;
        }

        _handle = HAuthTicket.Invalid;
        SteamUser.CancelAuthTicket(handle);
    }
}
