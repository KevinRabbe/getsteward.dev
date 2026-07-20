using System.Buffers.Binary;
using System.Net.Sockets;
using System.Text;

namespace SharedWorlds.GameAdapters.Factorio;

/// <summary>
/// Minimal Source RCON client used only for local Factorio server lifecycle control.
/// The adapter keeps RCON bound to loopback and uses an ephemeral random password per hosted session.
/// </summary>
internal static class FactorioRconClient
{
    private const int AuthPacketType = 3;
    private const int AuthResponsePacketType = 2;
    private const int ExecuteCommandPacketType = 2;
    private const int MaxPacketSize = 1024 * 1024;

    public static async Task WaitUntilReadyAsync(
        string host,
        int port,
        string password,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        Exception? lastFailure = null;

        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                await AuthenticateAsync(host, port, password, cancellationToken);
                return;
            }
            catch (Exception exception) when (
                exception is SocketException or IOException or InvalidDataException)
            {
                lastFailure = exception;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken);
        }

        throw new TimeoutException(
            $"Factorio dedicated server did not expose a ready RCON endpoint on {host}:{port} within {timeout.TotalSeconds:0} seconds.",
            lastFailure);
    }

    public static async Task<string> ExecuteAsync(
        string host,
        int port,
        string password,
        string command,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command);

        using var client = new TcpClient();
        await client.ConnectAsync(host, port, cancellationToken);
        await using var stream = client.GetStream();

        await AuthenticateAsync(stream, password, cancellationToken);

        const int commandRequestId = 2;
        await WritePacketAsync(
            stream,
            commandRequestId,
            ExecuteCommandPacketType,
            command,
            cancellationToken);

        for (var attempt = 0; attempt < 4; attempt++)
        {
            var response = await ReadPacketAsync(stream, cancellationToken);
            if (response.RequestId == commandRequestId)
            {
                return response.Body;
            }
        }

        throw new InvalidDataException("Factorio RCON did not return a response for the command request.");
    }

    private static async Task AuthenticateAsync(
        string host,
        int port,
        string password,
        CancellationToken cancellationToken)
    {
        using var client = new TcpClient();
        await client.ConnectAsync(host, port, cancellationToken);
        await using var stream = client.GetStream();
        await AuthenticateAsync(stream, password, cancellationToken);
    }

    private static async Task AuthenticateAsync(
        NetworkStream stream,
        string password,
        CancellationToken cancellationToken)
    {
        const int authRequestId = 1;
        await WritePacketAsync(stream, authRequestId, AuthPacketType, password, cancellationToken);

        // Source RCON implementations may emit an empty response-value packet before the auth response.
        for (var attempt = 0; attempt < 4; attempt++)
        {
            var response = await ReadPacketAsync(stream, cancellationToken);
            if (response.PacketType != AuthResponsePacketType)
            {
                continue;
            }

            if (response.RequestId == -1)
            {
                throw new InvalidDataException("Factorio RCON authentication was rejected.");
            }

            if (response.RequestId != authRequestId)
            {
                throw new InvalidDataException("Factorio RCON returned an unexpected authentication request ID.");
            }

            return;
        }

        throw new InvalidDataException("Factorio RCON did not return an authentication response.");
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

    private static async Task<RconPacket> ReadPacketAsync(
        NetworkStream stream,
        CancellationToken cancellationToken)
    {
        var sizeBuffer = new byte[sizeof(int)];
        await ReadExactlyAsync(stream, sizeBuffer, cancellationToken);
        var payloadLength = BinaryPrimitives.ReadInt32LittleEndian(sizeBuffer);
        if (payloadLength < 10 || payloadLength > MaxPacketSize)
        {
            throw new InvalidDataException($"Factorio RCON returned invalid packet length {payloadLength}.");
        }

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
                throw new EndOfStreamException("Factorio RCON connection closed unexpectedly.");
            }

            offset += read;
        }
    }

    private sealed record RconPacket(int RequestId, int PacketType, string Body);
}
