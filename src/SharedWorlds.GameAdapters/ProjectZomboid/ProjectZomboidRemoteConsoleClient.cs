using System.Net;
using System.Net.Sockets;

namespace SharedWorlds.GameAdapters.ProjectZomboid;

internal sealed class ProjectZomboidRemoteConsoleAuthenticationException : IOException
{
    public ProjectZomboidRemoteConsoleAuthenticationException()
        : base("Project Zomboid remote-console authentication was rejected.")
    {
    }
}

internal sealed class ProjectZomboidRemoteConsoleClient : IAsyncDisposable
{
    private const int MaximumPacketsPerOperation = 8;
    private readonly TcpClient _tcpClient;
    private readonly NetworkStream _stream;
    private readonly TimeSpan _operationTimeout;
    private int _nextRequestId = 1;
    private bool _disposed;

    private ProjectZomboidRemoteConsoleClient(
        TcpClient tcpClient,
        TimeSpan operationTimeout)
    {
        _tcpClient = tcpClient;
        _stream = tcpClient.GetStream();
        _operationTimeout = operationTimeout;
    }

    internal static async Task<ProjectZomboidRemoteConsoleClient> ConnectAsync(
        int port,
        string password,
        TimeSpan operationTimeout,
        CancellationToken cancellationToken)
    {
        if (port is < 1 or > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(port));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(password);
        if (operationTimeout <= TimeSpan.Zero || operationTimeout == Timeout.InfiniteTimeSpan)
        {
            throw new ArgumentOutOfRangeException(nameof(operationTimeout));
        }

        var tcpClient = new TcpClient(AddressFamily.InterNetwork);
        try
        {
            await WithTimeoutAsync(
                token => tcpClient.ConnectAsync(IPAddress.Loopback, port, token).AsTask(),
                operationTimeout,
                cancellationToken);
            var client = new ProjectZomboidRemoteConsoleClient(tcpClient, operationTimeout);
            await client.AuthenticateAsync(password, cancellationToken);
            return client;
        }
        catch
        {
            tcpClient.Dispose();
            throw;
        }
    }

    internal async Task<string> ExecuteAsync(
        string command,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(command);
        if (command.Length > 1024 || command.IndexOfAny(['\0', '\r', '\n']) >= 0)
        {
            throw new ArgumentException(
                "Project Zomboid remote-console command is invalid or exceeds Steward's command bound.",
                nameof(command));
        }

        var requestId = NextRequestId();
        await WritePacketAsync(
            requestId,
            ProjectZomboidRemoteConsoleProtocol.ExecuteCommandType,
            command,
            cancellationToken);

        for (var index = 0; index < MaximumPacketsPerOperation; index++)
        {
            var response = await ReadPacketAsync(cancellationToken);
            if (response.RequestId == requestId &&
                response.Type == ProjectZomboidRemoteConsoleProtocol.ResponseValueType)
            {
                return response.Body;
            }
        }

        throw new InvalidDataException(
            "Project Zomboid remote console did not return a bounded matching command response.");
    }

    private async Task AuthenticateAsync(
        string password,
        CancellationToken cancellationToken)
    {
        var requestId = NextRequestId();
        await WritePacketAsync(
            requestId,
            ProjectZomboidRemoteConsoleProtocol.AuthenticationType,
            password,
            cancellationToken);

        for (var index = 0; index < MaximumPacketsPerOperation; index++)
        {
            var response = await ReadPacketAsync(cancellationToken);
            if (response.Type != ProjectZomboidRemoteConsoleProtocol.AuthenticationResponseType)
            {
                continue;
            }

            if (response.RequestId == -1)
            {
                throw new ProjectZomboidRemoteConsoleAuthenticationException();
            }

            if (response.RequestId == requestId)
            {
                return;
            }
        }

        throw new InvalidDataException(
            "Project Zomboid remote console did not return a bounded authentication response.");
    }

    private async Task WritePacketAsync(
        int requestId,
        int type,
        string body,
        CancellationToken cancellationToken)
    {
        var bytes = ProjectZomboidRemoteConsoleProtocol.Encode(requestId, type, body);
        await WithTimeoutAsync(
            async token =>
            {
                await _stream.WriteAsync(bytes, token);
                await _stream.FlushAsync(token);
            },
            _operationTimeout,
            cancellationToken);
    }

    private Task<ProjectZomboidRemoteConsolePacket> ReadPacketAsync(
        CancellationToken cancellationToken)
        => WithTimeoutAsync(
            token => ProjectZomboidRemoteConsoleProtocol.ReadAsync(_stream, token),
            _operationTimeout,
            cancellationToken);

    private int NextRequestId()
    {
        if (_nextRequestId == int.MaxValue)
        {
            _nextRequestId = 1;
        }

        return _nextRequestId++;
    }

    private static async Task WithTimeoutAsync(
        Func<CancellationToken, Task> action,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using var timeoutCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCancellation.CancelAfter(timeout);
        try
        {
            await action(timeoutCancellation.Token);
        }
        catch (OperationCanceledException) when (
            !cancellationToken.IsCancellationRequested &&
            timeoutCancellation.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"Project Zomboid remote-console operation made no progress for {timeout}.");
        }
    }

    private static async Task<T> WithTimeoutAsync<T>(
        Func<CancellationToken, Task<T>> action,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using var timeoutCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCancellation.CancelAfter(timeout);
        try
        {
            return await action(timeoutCancellation.Token);
        }
        catch (OperationCanceledException) when (
            !cancellationToken.IsCancellationRequested &&
            timeoutCancellation.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"Project Zomboid remote-console operation made no progress for {timeout}.");
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await _stream.DisposeAsync();
        _tcpClient.Dispose();
    }
}
