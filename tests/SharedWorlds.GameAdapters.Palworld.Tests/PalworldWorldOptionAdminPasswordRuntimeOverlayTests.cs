using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace SharedWorlds.GameAdapters.Palworld.Tests;

public sealed class PalworldWorldOptionAdminPasswordRuntimeOverlayTests
{
    [Fact]
    public void CurrentPlMContainerUsesOodleCodecAndPatchesOnlyAdminPassword()
    {
        var codec = new TestOodleCodec();
        var originalPayload = BuildGvasPayload(string.Empty);
        var original = WrapPlM(originalPayload, codec);

        var result = PalworldWorldOptionAdminPasswordRuntimeOverlay.Create(
            original,
            "temporary-steward-password",
            codec);

        Assert.True(PalworldWorldOptionAdminPasswordRuntimeOverlay.RequiresOodle(original));
        Assert.Equal("PlM/0x31", PalworldWorldOptionAdminPasswordRuntimeOverlay.DescribeContainer(original));
        var patchedPayload = UnwrapPlM(result.PatchedSave, codec);
        Assert.Equal("temporary-steward-password", ReadAdminPassword(patchedPayload));
        Assert.Equal(originalPayload.Length, result.OriginalPayloadLength);
        Assert.NotEqual(result.OriginalSaveSha256, result.PatchedSaveSha256);
    }

    [Fact]
    public void PlMWithoutOodleFailsClosed()
    {
        var codec = new TestOodleCodec();
        var source = WrapPlM(BuildGvasPayload(string.Empty), codec);

        var exception = Assert.Throws<InvalidDataException>(() =>
            PalworldWorldOptionAdminPasswordRuntimeOverlay.Create(source, "temporary-password"));

        Assert.Contains("Oodle", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void UnsupportedPlMSaveTypeFailsClosed()
    {
        var codec = new TestOodleCodec();
        var source = WrapPlM(BuildGvasPayload(string.Empty), codec);
        source[11] = 0x32;

        var exception = Assert.Throws<InvalidDataException>(() =>
            PalworldWorldOptionAdminPasswordRuntimeOverlay.Create(source, "temporary-password", codec));

        Assert.Contains("unsupported", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void UnknownMagicFailsClosedAndReportsObservedBytes()
    {
        var source = WrapPlM(BuildGvasPayload(string.Empty), new TestOodleCodec());
        "XYZ"u8.CopyTo(source.AsSpan(8, 3));

        var exception = Assert.Throws<InvalidDataException>(() =>
            PalworldWorldOptionAdminPasswordRuntimeOverlay.Create(source, "temporary-password"));

        Assert.Contains("58595A", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void LegacyPlZStillUsesExistingOverlayPath()
    {
        var source = WrapPlZ(BuildGvasPayload(string.Empty));

        var result = PalworldWorldOptionAdminPasswordRuntimeOverlay.Create(
            source,
            "temporary-password");

        Assert.False(PalworldWorldOptionAdminPasswordRuntimeOverlay.RequiresOodle(source));
        Assert.Equal("PlZ/0x31", PalworldWorldOptionAdminPasswordRuntimeOverlay.DescribeContainer(source));
        Assert.Equal("temporary-password", ReadAdminPassword(UnwrapPlZ(result.PatchedSave)));
    }

    private static byte[] BuildGvasPayload(string password)
    {
        var payload = new List<byte>();
        payload.AddRange("GVAS"u8.ToArray());
        payload.AddRange([0x01, 0x02, 0x03, 0x04]);
        payload.AddRange(EncodeFString("AdminPassword"));
        payload.AddRange(EncodeFString("StrProperty"));
        var value = EncodeFString(password);
        var size = new byte[sizeof(ulong)];
        BinaryPrimitives.WriteUInt64LittleEndian(size, checked((ulong)value.Length));
        payload.AddRange(size);
        payload.Add(0);
        payload.AddRange(value);
        payload.AddRange([0xDE, 0xAD, 0xBE, 0xEF]);
        return payload.ToArray();
    }

    private static byte[] WrapPlM(ReadOnlySpan<byte> payload, IPalworldOodleCodec codec)
    {
        var compressed = codec.CompressMermaid(payload);
        return Wrap(payload.Length, compressed, "PlM"u8, 0x31);
    }

    private static byte[] WrapPlZ(ReadOnlySpan<byte> payload)
    {
        var compressed = Compress(payload);
        return Wrap(payload.Length, compressed, "PlZ"u8, 0x31);
    }

    private static byte[] Wrap(int uncompressedLength, byte[] compressed, ReadOnlySpan<byte> magic, byte saveType)
    {
        var result = new byte[12 + compressed.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(0, 4), checked((uint)uncompressedLength));
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(4, 4), checked((uint)compressed.Length));
        magic.CopyTo(result.AsSpan(8, 3));
        result[11] = saveType;
        compressed.CopyTo(result, 12);
        return result;
    }

    private static byte[] UnwrapPlM(ReadOnlySpan<byte> save, IPalworldOodleCodec codec)
    {
        var length = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(save[..4]));
        return codec.Decompress(save[12..], length);
    }

    private static byte[] UnwrapPlZ(ReadOnlySpan<byte> save)
        => Decompress(save[12..]);

    private static string ReadAdminPassword(ReadOnlySpan<byte> payload)
    {
        var name = EncodeFString("AdminPassword");
        var type = EncodeFString("StrProperty");
        var offset = payload.IndexOf(name);
        Assert.True(offset >= 0);
        var cursor = offset + name.Length;
        Assert.True(payload.Slice(cursor, type.Length).SequenceEqual(type));
        cursor += type.Length + sizeof(ulong);
        Assert.Equal(0, payload[cursor++]);
        var serializedLength = BinaryPrimitives.ReadInt32LittleEndian(payload.Slice(cursor, sizeof(int)));
        Assert.True(serializedLength > 0);
        return Encoding.UTF8.GetString(payload.Slice(cursor + sizeof(int), serializedLength - 1));
    }

    private static byte[] EncodeFString(string value)
    {
        if (value.Length == 0)
        {
            return new byte[sizeof(int)];
        }

        var text = Encoding.UTF8.GetBytes(value);
        var result = new byte[sizeof(int) + text.Length + 1];
        BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(0, sizeof(int)), text.Length + 1);
        text.CopyTo(result, sizeof(int));
        return result;
    }

    private static byte[] Compress(ReadOnlySpan<byte> payload)
    {
        using var destination = new MemoryStream();
        using (var zlib = new ZLibStream(destination, CompressionLevel.Optimal, leaveOpen: true))
        {
            zlib.Write(payload);
        }

        return destination.ToArray();
    }

    private static byte[] Decompress(ReadOnlySpan<byte> compressed)
    {
        using var source = new MemoryStream(compressed.ToArray(), writable: false);
        using var zlib = new ZLibStream(source, CompressionMode.Decompress);
        using var destination = new MemoryStream();
        zlib.CopyTo(destination);
        return destination.ToArray();
    }

    private sealed class TestOodleCodec : IPalworldOodleCodec
    {
        public byte[] Decompress(ReadOnlySpan<byte> compressed, int uncompressedLength)
        {
            var result = PalworldWorldOptionAdminPasswordRuntimeOverlayTests.Decompress(compressed);
            Assert.Equal(uncompressedLength, result.Length);
            return result;
        }

        public byte[] CompressMermaid(ReadOnlySpan<byte> payload)
            => Compress(payload);
    }
}
