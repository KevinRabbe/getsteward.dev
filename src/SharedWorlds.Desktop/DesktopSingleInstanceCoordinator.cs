using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;

namespace SharedWorlds.Desktop;

internal sealed record DesktopActivationRequest(string? PortableWorldPath);

/// <summary>
/// Owns the process boundary for the local desktop store. Exactly one Safe World desktop process
/// may be primary for a user/session; later launches can only hand a bounded activation request to
/// that primary process and then exit before constructing storage/runtime state.
/// </summary>
internal sealed class DesktopSingleInstanceCoordinator : IDisposable
{
    private const string DefaultInstanceName = "SafeWorld.Desktop.Primary.v1";
    private const string DefaultPipePrefix = "SafeWorld.Desktop.Activation.v1";
    private const int MaximumActivationBytes = 32 * 1024;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    private readonly Mutex _mutex;
    private readonly string _pipeName;
    private readonly CancellationTokenSource _listenerCancellation = new();
    private Task? _listenerTask;
    private bool _disposed;

    private DesktopSingleInstanceCoordinator(
        Mutex mutex,
        bool isPrimary,
        string pipeName)
    {
        _mutex = mutex;
        IsPrimary = isPrimary;
        _pipeName = pipeName;
    }

    internal bool IsPrimary { get; }

    internal static DesktopSingleInstanceCoordinator CreateForCurrentSession()
    {
        var sessionId = Process.GetCurrentProcess().SessionId;
        return Create(
            DefaultInstanceName,
            $"{DefaultPipePrefix}.{sessionId}");
    }

    internal static DesktopSingleInstanceCoordinator Create(
        string instanceName,
        string pipeName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceName);
        ArgumentException.ThrowIfNullOrWhiteSpace(pipeName);

        var options = new NamedWaitHandleOptions
        {
            CurrentUserOnly = true,
            CurrentSessionOnly = true
        };
        var mutex = new Mutex(
            initiallyOwned: true,
            instanceName,
            options,
            out var createdNew);
        return new DesktopSingleInstanceCoordinator(mutex, createdNew, pipeName);
    }

    internal void StartListening(Func<DesktopActivationRequest, Task> activationHandler)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(activationHandler);
        if (!IsPrimary)
        {
            throw new InvalidOperationException("Only the primary Safe World process may listen for activations.");
        }

        if (_listenerTask is not null)
        {
            throw new InvalidOperationException("Safe World activation listening has already started.");
        }

        _listenerTask = ListenAsync(activationHandler, _listenerCancellation.Token);
    }

    internal bool TryForward(
        DesktopActivationRequest request,
        int timeoutMilliseconds = 3000)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(request);
        if (IsPrimary)
        {
            throw new InvalidOperationException("The primary Safe World process cannot forward to itself.");
        }

        if (timeoutMilliseconds <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(timeoutMilliseconds));
        }

        var portablePath = request.PortableWorldPath;
        if (portablePath is not null)
        {
            portablePath = PortableWorldStartupActivation.ResolvePath([portablePath]);
            if (portablePath is null)
            {
                return false;
            }
        }

        var payload = portablePath is null
            ? Array.Empty<byte>()
            : StrictUtf8.GetBytes(portablePath);
        if (payload.Length > MaximumActivationBytes)
        {
            return false;
        }

        try
        {
            using var client = new NamedPipeClientStream(
                ".",
                _pipeName,
                PipeDirection.InOut,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            client.Connect(timeoutMilliseconds);

            Span<byte> header = stackalloc byte[sizeof(int)];
            BinaryPrimitives.WriteInt32LittleEndian(header, payload.Length);
            client.Write(header);
            if (payload.Length > 0)
            {
                client.Write(payload);
            }

            client.Flush();

            using var acknowledgementCancellation = new CancellationTokenSource(timeoutMilliseconds);
            var acknowledgement = new byte[1];
            var read = client.ReadAsync(
                    acknowledgement.AsMemory(),
                    acknowledgementCancellation.Token)
                .AsTask()
                .GetAwaiter()
                .GetResult();
            return read == 1 && acknowledgement[0] == 1;
        }
        catch (Exception exception) when (
            exception is TimeoutException or IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            return false;
        }
    }

    private async Task ListenAsync(
        Func<DesktopActivationRequest, Task> activationHandler,
        CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await using var server = new NamedPipeServerStream(
                    _pipeName,
                    PipeDirection.InOut,
                    maxNumberOfServerInstances: 1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await server.WaitForConnectionAsync(cancellationToken);

                var request = await ReadRequestAsync(server, cancellationToken);
                await activationHandler(request);
                await server.WriteAsync(new byte[] { 1 }, cancellationToken);
                await server.FlushAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException or InvalidDataException or DecoderFallbackException)
            {
                // One malformed or interrupted local activation must not kill the primary listener.
                // The next loop creates a fresh single-client pipe instance.
            }
        }
    }

    private static async Task<DesktopActivationRequest> ReadRequestAsync(
        Stream stream,
        CancellationToken cancellationToken)
    {
        var header = new byte[sizeof(int)];
        await stream.ReadExactlyAsync(header, cancellationToken);
        var length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length < 0 || length > MaximumActivationBytes)
        {
            throw new InvalidDataException("Safe World activation payload length is outside the allowed bounds.");
        }

        if (length == 0)
        {
            return new DesktopActivationRequest(PortableWorldPath: null);
        }

        var payload = new byte[length];
        await stream.ReadExactlyAsync(payload, cancellationToken);
        var rawPath = StrictUtf8.GetString(payload);
        var portablePath = PortableWorldStartupActivation.ResolvePath([rawPath]);
        if (portablePath is null)
        {
            throw new InvalidDataException("Safe World activation did not contain one portable World path.");
        }

        return new DesktopActivationRequest(portablePath);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _listenerCancellation.Cancel();
        _listenerCancellation.Dispose();

        if (IsPrimary)
        {
            try
            {
                _mutex.ReleaseMutex();
            }
            catch (ApplicationException)
            {
                // The process is exiting. Do not replace shutdown with a secondary mutex error.
            }
        }

        _mutex.Dispose();
    }
}
