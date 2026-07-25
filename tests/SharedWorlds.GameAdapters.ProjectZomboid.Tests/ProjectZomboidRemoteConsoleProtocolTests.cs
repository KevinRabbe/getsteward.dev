using System.Buffers.Binary;

namespace SharedWorlds.GameAdapters.ProjectZomboid.Tests;

public sealed class ProjectZomboidRemoteConsoleProtocolTests
{
    [Fact]
    public async Task PacketRoundTripsLittleEndianSourceRconShape()
    {
        var bytes = ProjectZomboidRemoteConsoleProtocol.Encode(
            requestId: 42,
            type: ProjectZomboidRemoteConsoleProtocol.ExecuteCommandType,
            body: "save");

        Assert.Equal(14, BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(0, 4)));
        await using var stream = new MemoryStream(bytes, writable: false);
        var packet = await ProjectZomboidRemoteConsoleProtocol.ReadAsync(
            stream,
            CancellationToken.None);

        Assert.Equal(42, packet.RequestId);
        Assert.Equal(ProjectZomboidRemoteConsoleProtocol.ExecuteCommandType, packet.Type);
        Assert.Equal("save", packet.Body);
    }

    [Fact]
    public async Task OversizedDeclaredPacketIsRejectedBeforeAllocation()
    {
        var bytes = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, 4 * 1024 * 1024 + 11);
        await using var stream = new MemoryStream(bytes, writable: false);

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
            ProjectZomboidRemoteConsoleProtocol.ReadAsync(stream, CancellationToken.None));

        Assert.Contains("outside Steward's safety bounds", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MissingPacketTerminatorsAreRejected()
    {
        var bytes = ProjectZomboidRemoteConsoleProtocol.Encode(1, 0, "ok");
        bytes[^1] = 1;
        await using var stream = new MemoryStream(bytes, writable: false);

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
            ProjectZomboidRemoteConsoleProtocol.ReadAsync(stream, CancellationToken.None));

        Assert.Contains("terminators", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void OutboundBodyContainingNullIsRejected()
    {
        Assert.Throws<ArgumentException>(() =>
            ProjectZomboidRemoteConsoleProtocol.Encode(1, 2, "save\0quit"));
    }
}
