using System.Buffers.Binary;
using System.Text;

namespace SharedWorlds.GameAdapters.ProjectZomboid;

internal sealed record ProjectZomboidRemoteConsolePacket(
    int RequestId,
    int Type,
    string Body);

internal static class ProjectZomboidRemoteConsoleProtocol
{
    internal const int ResponseValueType = 0;
    internal const int ExecuteCommandType = 2;
    internal const int AuthenticationResponseType = 2;
    internal const int AuthenticationType = 3;

    private const int MinimumPacketLength = 10;
    private const int MaximumBodyBytes = 4 * 1024 * 1024;
    private const int MaximumPacketLength = MinimumPacketLength + MaximumBodyBytes;
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    internal static byte[] Encode(int requestId, int type, string body)
    {
        ArgumentNullException.ThrowIfNull(body);
        if (body.IndexOf('\0') >= 0)
        {
            throw new ArgumentException(
                "Project Zomboid remote-console packet body must not contain NUL characters.",
                nameof(body));
        }

        var bodyBytes = StrictUtf8.GetBytes(body);
        if (bodyBytes.Length > MaximumBodyBytes)
        {
            throw new ArgumentOutOfRangeException(
                nameof(body),
                $"Project Zomboid remote-console packet body exceeds {MaximumBodyBytes} bytes.");
        }

        var packetLength = checked(MinimumPacketLength + bodyBytes.Length);
        var bytes = new byte[checked(sizeof(int) + packetLength)];
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(0, 4), packetLength);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(4, 4), requestId);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(8, 4), type);
        bodyBytes.CopyTo(bytes.AsSpan(12));
        // The final two bytes are the Source RCON body terminator and empty-string terminator.
        return bytes;
    }

    internal static async Task<ProjectZomboidRemoteConsolePacket> ReadAsync(
        Stream source,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);

        var lengthBytes = new byte[4];
        await source.ReadExactlyAsync(lengthBytes, cancellationToken);
        var packetLength = BinaryPrimitives.ReadInt32LittleEndian(lengthBytes);
        if (packetLength is < MinimumPacketLength or > MaximumPacketLength)
        {
            throw new InvalidDataException(
                $"Project Zomboid remote-console packet length {packetLength} is outside Steward's safety bounds.");
        }

        var payload = new byte[packetLength];
        await source.ReadExactlyAsync(payload, cancellationToken);
        if (payload[^1] != 0 || payload[^2] != 0)
        {
            throw new InvalidDataException(
                "Project Zomboid remote-console packet is missing its required terminators.");
        }

        var requestId = BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(0, 4));
        var type = BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(4, 4));
        string body;
        try
        {
            body = StrictUtf8.GetString(payload.AsSpan(8, packetLength - MinimumPacketLength));
        }
        catch (DecoderFallbackException exception)
        {
            throw new InvalidDataException(
                "Project Zomboid remote-console packet body is not valid UTF-8.",
                exception);
        }

        return new ProjectZomboidRemoteConsolePacket(requestId, type, body);
    }
}
