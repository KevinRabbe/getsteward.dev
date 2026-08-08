using System.IO;
using SharedWorlds.Core.Domain;
using Steamworks;

namespace SharedWorlds.Desktop;

internal sealed class SteamWebApiTicketSource : IDisposable
{
    private static readonly TimeSpan TicketTimeout = TimeSpan.FromSeconds(15);

    private readonly SteamPlatformRuntime _platform;
    private readonly SemaphoreSlim _requestGate = new(1, 1);
    private readonly Callback<GetTicketForWebApiResponse_t> _ticketCallback;
    private TaskCompletionSource<GetTicketForWebApiResponse_t>? _pendingCompletion;
    private HAuthTicket _pendingHandle = HAuthTicket.Invalid;
    private bool _disposed;

    public SteamWebApiTicketSource(SteamPlatformRuntime platform)
    {
        ArgumentNullException.ThrowIfNull(platform);
        _platform = platform;
        _ticketCallback = Callback<GetTicketForWebApiResponse_t>.Create(OnTicketResponse);
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

            GetTicketForWebApiResponse_t response;
            try
            {
                response = await completion.Task.WaitAsync(timeout.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException("Steam did not return a Web API authentication ticket in time.");
            }

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

            var ticketHex = Convert.ToHexString(
                response.m_rgubTicket.AsSpan(0, response.m_cubTicket));

            var lease = new SteamWebApiTicketLease(
                issuedHandle,
                ticketHex,
                _platform.LocalUser);
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
