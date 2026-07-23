using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using SharedWorlds.GameAdapters.Factorio;

namespace SharedWorlds.GameAdapters.Factorio.Tests;

public sealed class FactorioRconClientTests
{
    [Fact]
    public async Task WaitUntilReadyAsyncRequiresSuccessfulRconAuthentication()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var server = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync(timeout.Token);
            await using var stream = client.GetStream();
            var auth = await ReadPacketAsync(stream, timeout.Token);

            Assert.Equal(1, auth.RequestId);
            Assert.Equal(3, auth.PacketType);
            Assert.Equal("ready-secret", auth.Body);

            await WritePacketAsync(
                stream,
                requestId: 1,
                packetType: 2,
                body: string.Empty,
                timeout.Token);
        }, timeout.Token);

        await FactorioRconClient.WaitUntilReadyAsync(
            "127.0.0.1",
            port,
            "ready-secret",
            TimeSpan.FromSeconds(2),
            timeout.Token);
        await server;
    }

    [Fact]
    public async Task ExecuteAsyncAuthenticatesThenSendsServerSaveCommand()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var server = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync(timeout.Token);
            await using var stream = client.GetStream();

            var auth = await ReadPacketAsync(stream, timeout.Token);
            Assert.Equal(1, auth.RequestId);
            Assert.Equal(3, auth.PacketType);
            Assert.Equal("save-secret", auth.Body);
            await WritePacketAsync(
                stream,
                requestId: 1,
                packetType: 2,
                body: string.Empty,
                timeout.Token);

            var command = await ReadPacketAsync(stream, timeout.Token);
            Assert.Equal(2, command.RequestId);
            Assert.Equal(2, command.PacketType);
            Assert.Equal("/server-save", command.Body);
            await WritePacketAsync(
                stream,
                requestId: 2,
                packetType: 0,
                body: "Saving map",
                timeout.Token);
        }, timeout.Token);

        var response = await FactorioRconClient.ExecuteAsync(
            "127.0.0.1",
            port,
            "save-secret",
            "/server-save",
            timeout.Token);

        Assert.Equal("Saving map", response);
        await server;
    }

    private static async Task<RconPacket> ReadPacketAsync(
        NetworkStream stream,
        CancellationToken cancellationToken)
    {
        var sizeBytes = new byte[sizeof(int)];
        await ReadExactlyAsync(stream, sizeBytes, cancellationToken);
        var payloadLength = BinaryPrimitives.ReadInt32LittleEndian(sizeBytes);
        Assert.InRange(payloadLength, 10, 1024 * 1024);

        var payload = new byte[payloadLength];
        await ReadExactlyAsync(stream, payload, cancellationToken);
        var requestId = BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(0, sizeof(int)));
        var packetType = BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(4, sizeof(int)));
        var bodyLength = payloadLength - 10;
        var body = bodyLength == 0
            ? string.Empty
            : Encoding.UTF8.GetString(payload, 8, bodyLength);
        return new RconPacket(requestId, packetType, body);
    }

    private static async Task WritePacketAsync(
        NetworkStream stream,
        int requestId,
        int packetType,
        string body,
        CancellationToken cancellationToken)
    {
        var bodyBytes = Encoding.UTF8.GetBytes(body);
        var payloadLength = sizeof(int) + sizeof(int) + bodyBytes.Length + 2;
        var packet = new byte[sizeof(int) + payloadLength];
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(0, sizeof(int)), payloadLength);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(4, sizeof(int)), requestId);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(8, sizeof(int)), packetType);
        bodyBytes.CopyTo(packet.AsSpan(12));
        await stream.WriteAsync(packet, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    private static async Task ReadExactlyAsync(
        NetworkStream stream,
        Memory<byte> buffer,
        CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer[offset..], cancellationToken);
            if (read == 0)
            {
                throw new EndOfStreamException("Test RCON peer closed unexpectedly.");
            }

            offset += read;
        }
    }

    private sealed record RconPacket(int RequestId, int PacketType, string Body);
}
