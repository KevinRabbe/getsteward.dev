using System.Net;
using System.Net.Sockets;

namespace SharedWorlds.GameAdapters.ProjectZomboid.Tests;

public sealed class ProjectZomboidRemoteConsoleClientTests
{
    [Fact]
    public async Task AuthenticatesAndExecutesBoundedCommandOverLoopback()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var serverTask = Task.Run(async () =>
        {
            using var server = await listener.AcceptTcpClientAsync();
            await using var stream = server.GetStream();

            var auth = await ProjectZomboidRemoteConsoleProtocol.ReadAsync(stream, CancellationToken.None);
            Assert.Equal(ProjectZomboidRemoteConsoleProtocol.AuthenticationType, auth.Type);
            Assert.Equal("transient-secret", auth.Body);
            await WriteAsync(stream, auth.RequestId, ProjectZomboidRemoteConsoleProtocol.ResponseValueType, string.Empty);
            await WriteAsync(stream, auth.RequestId, ProjectZomboidRemoteConsoleProtocol.AuthenticationResponseType, string.Empty);

            var command = await ProjectZomboidRemoteConsoleProtocol.ReadAsync(stream, CancellationToken.None);
            Assert.Equal(ProjectZomboidRemoteConsoleProtocol.ExecuteCommandType, command.Type);
            Assert.Equal("save", command.Body);
            await WriteAsync(stream, command.RequestId, ProjectZomboidRemoteConsoleProtocol.ResponseValueType, "World saved");
        });

        await using var client = await ProjectZomboidRemoteConsoleClient.ConnectAsync(
            port,
            "transient-secret",
            TimeSpan.FromSeconds(5),
            CancellationToken.None);
        var response = await client.ExecuteAsync("save", CancellationToken.None);

        Assert.Equal("World saved", response);
        await serverTask;
    }

    [Fact]
    public async Task AuthenticationRejectionDoesNotExposePassword()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var serverTask = Task.Run(async () =>
        {
            using var server = await listener.AcceptTcpClientAsync();
            await using var stream = server.GetStream();
            var auth = await ProjectZomboidRemoteConsoleProtocol.ReadAsync(stream, CancellationToken.None);
            await WriteAsync(stream, -1, ProjectZomboidRemoteConsoleProtocol.AuthenticationResponseType, string.Empty);
        });

        var exception = await Assert.ThrowsAsync<ProjectZomboidRemoteConsoleAuthenticationException>(() =>
            ProjectZomboidRemoteConsoleClient.ConnectAsync(
                port,
                "do-not-leak-this",
                TimeSpan.FromSeconds(5),
                CancellationToken.None));

        Assert.DoesNotContain("do-not-leak-this", exception.ToString(), StringComparison.Ordinal);
        await serverTask;
    }

    [Fact]
    public async Task MissingServerResponseTimesOut()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var releaseServer = new CancellationTokenSource();
        var serverTask = Task.Run(async () =>
        {
            using var server = await listener.AcceptTcpClientAsync();
            try
            {
                // The client only needs a connected peer that never answers. Parsing its authentication
                // packet here creates an unrelated scheduling race: under load the client's bounded
                // timeout may close the socket before this fake server gets CPU time to read it.
                await Task.Delay(Timeout.InfiniteTimeSpan, releaseServer.Token);
            }
            catch (OperationCanceledException)
            {
            }
        });

        try
        {
            var exception = await Assert.ThrowsAsync<TimeoutException>(() =>
                ProjectZomboidRemoteConsoleClient.ConnectAsync(
                    port,
                    "transient-secret",
                    TimeSpan.FromMilliseconds(200),
                    CancellationToken.None));

            Assert.Contains("made no progress", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            releaseServer.Cancel();
            await serverTask;
        }
    }

    [Fact]
    public async Task CallerCancellationIsNotRelabeledAsTimeout()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var releaseServer = new CancellationTokenSource();
        var authenticationReceived = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var serverTask = Task.Run(async () =>
        {
            using var server = await listener.AcceptTcpClientAsync();
            await using var stream = server.GetStream();
            _ = await ProjectZomboidRemoteConsoleProtocol.ReadAsync(stream, CancellationToken.None);
            authenticationReceived.TrySetResult(true);
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, releaseServer.Token);
            }
            catch (OperationCanceledException)
            {
            }
        });
        using var cancellation = new CancellationTokenSource();

        try
        {
            var connectTask = ProjectZomboidRemoteConsoleClient.ConnectAsync(
                port,
                "transient-secret",
                TimeSpan.FromSeconds(10),
                cancellation.Token);

            await authenticationReceived.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cancellation.Cancel();

            var exception = await Record.ExceptionAsync(() => connectTask);

            Assert.IsAssignableFrom<OperationCanceledException>(exception);
            Assert.IsNotType<TimeoutException>(exception);
        }
        finally
        {
            releaseServer.Cancel();
            await serverTask;
        }
    }

    [Theory]
    [InlineData("save\nquit")]
    [InlineData("save\0quit")]
    public async Task InvalidCommandIsRejectedBeforeNetworkWrite(string command)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var serverTask = Task.Run(async () =>
        {
            using var server = await listener.AcceptTcpClientAsync();
            await using var stream = server.GetStream();
            var auth = await ProjectZomboidRemoteConsoleProtocol.ReadAsync(stream, CancellationToken.None);
            await WriteAsync(stream, auth.RequestId, ProjectZomboidRemoteConsoleProtocol.AuthenticationResponseType, string.Empty);
            await Task.Delay(200);
        });

        await using var client = await ProjectZomboidRemoteConsoleClient.ConnectAsync(
            port,
            "transient-secret",
            TimeSpan.FromSeconds(5),
            CancellationToken.None);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            client.ExecuteAsync(command, CancellationToken.None));
        await serverTask;
    }

    [Fact]
    public async Task RejectsMoreThanBoundedIrrelevantPackets()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var serverTask = Task.Run(async () =>
        {
            using var server = await listener.AcceptTcpClientAsync();
            await using var stream = server.GetStream();
            var auth = await ProjectZomboidRemoteConsoleProtocol.ReadAsync(stream, CancellationToken.None);
            for (var index = 0; index < 8; index++)
            {
                await WriteAsync(stream, auth.RequestId + 1, ProjectZomboidRemoteConsoleProtocol.ResponseValueType, "noise");
            }
        });

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
            ProjectZomboidRemoteConsoleClient.ConnectAsync(
                port,
                "transient-secret",
                TimeSpan.FromSeconds(5),
                CancellationToken.None));

        Assert.Contains("bounded authentication response", exception.Message, StringComparison.Ordinal);
        await serverTask;
    }

    private static async Task WriteAsync(Stream stream, int requestId, int type, string body)
    {
        var bytes = ProjectZomboidRemoteConsoleProtocol.Encode(requestId, type, body);
        await stream.WriteAsync(bytes);
        await stream.FlushAsync();
    }
}
