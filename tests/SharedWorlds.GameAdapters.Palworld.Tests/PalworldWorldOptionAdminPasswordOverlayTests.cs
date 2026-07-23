using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace SharedWorlds.GameAdapters.Palworld.Tests;

public sealed class PalworldWorldOptionAdminPasswordOverlayTests
{
    [Fact]
    public void EmptyAdminPasswordCanBeOverlaidWithoutRewritingOtherPayloadData()
    {
        var source = BuildSave(string.Empty, 0x31);

        var result = PalworldWorldOptionAdminPasswordOverlay.Create(source, "temporary-steward-password");

        Assert.Equal(0x31, result.SaveType);
        Assert.False(result.ExistingAdminPasswordConfigured);
        Assert.NotEqual(result.OriginalSaveSha256, result.PatchedSaveSha256);
        Assert.Equal("temporary-steward-password", ReadAdminPassword(Decode(result.PatchedSave)));
    }

    [Fact]
    public void ExistingPasswordCanRoundTripThroughDoubleZlibWithoutChangingPayload()
    {
        var source = BuildSave("original-password", 0x32);
        var originalPayload = Decode(source);

        var temporary = PalworldWorldOptionAdminPasswordOverlay.Create(source, "temporary-password");
        var restored = PalworldWorldOptionAdminPasswordOverlay.Create(temporary.PatchedSave, "original-password");

        Assert.Equal(0x32, temporary.SaveType);
        Assert.True(temporary.ExistingAdminPasswordConfigured);
        Assert.Equal("temporary-password", ReadAdminPassword(Decode(temporary.PatchedSave)));
        Assert.Equal(originalPayload, Decode(restored.PatchedSave));
    }

    [Fact]
    public void UnicodeExistingPasswordIsParsedAndCanBeRestoredExactly()
    {
        var source = BuildSave("pässwörd-世界", 0x31);
        var originalPayload = Decode(source);

        var temporary = PalworldWorldOptionAdminPasswordOverlay.Create(source, "A1B2C3D4");
        var restored = PalworldWorldOptionAdminPasswordOverlay.Create(temporary.PatchedSave, "pässwörd-世界");

        Assert.Equal(originalPayload, Decode(restored.PatchedSave));
    }

    [Fact]
    public void MultipleStructurallyValidAdminPasswordPropertiesFailClosed()
    {
        var source = BuildSave(string.Empty, 0x31, duplicateProperty: true);

        var exception = Assert.Throws<InvalidDataException>(() =>
            PalworldWorldOptionAdminPasswordOverlay.Create(source, "temporary-password"));

        Assert.Contains("more than one", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void WrongPropertyTypeFailsClosed()
    {
        var source = BuildSave(string.Empty, 0x31, propertyType: "NameProperty");

        var exception = Assert.Throws<InvalidDataException>(() =>
            PalworldWorldOptionAdminPasswordOverlay.Create(source, "temporary-password"));

        Assert.Contains("does not contain", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void InvalidWrapperLengthFailsClosed()
    {
        var source = BuildSave(string.Empty, 0x31);
        BinaryPrimitives.WriteUInt32LittleEndian(source.AsSpan(4, 4), 1);

        var exception = Assert.Throws<InvalidDataException>(() =>
            PalworldWorldOptionAdminPasswordOverlay.Create(source, "temporary-password"));

        Assert.Contains("compressed length", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(0x30)]
    [InlineData(0x33)]
    public void UnknownOrUnsupportedSaveTypeFailsClosed(byte saveType)
    {
        var source = BuildSave(string.Empty, 0x31);
        source[11] = saveType;

        var exception = Assert.Throws<InvalidDataException>(() =>
            PalworldWorldOptionAdminPasswordOverlay.Create(source, "temporary-password"));

        Assert.Contains("unsupported", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static byte[] BuildSave(
        string password,
        byte saveType,
        bool duplicateProperty = false,
        string propertyType = "StrProperty")
    {
        var payload = new List<byte>
        {
            0x47, 0x56, 0x41, 0x53,
            0x01, 0x02, 0x03, 0x04
        };
        AddProperty(payload, password, propertyType);
        if (duplicateProperty)
        {
            payload.AddRange([0xAA, 0xBB, 0xCC]);
            AddProperty(payload, "second-password", propertyType);
        }

        payload.AddRange([0xDE, 0xAD, 0xBE, 0xEF]);
        return Wrap(payload.ToArray(), saveType);
    }

    private static void AddProperty(List<byte> payload, string password, string propertyType)
    {
        payload.AddRange(EncodeFString("AdminPassword"));
        payload.AddRange(EncodeFString(propertyType));
        var value = EncodeFString(password);
        var size = new byte[sizeof(ulong)];
        BinaryPrimitives.WriteUInt64LittleEndian(size, checked((ulong)value.Length));
        payload.AddRange(size);
        payload.Add(0);
        payload.AddRange(value);
    }

    private static string ReadAdminPassword(ReadOnlySpan<byte> payload)
    {
        var name = EncodeFString("AdminPassword");
        var type = EncodeFString("StrProperty");
        var nameOffset = payload.IndexOf(name);
        Assert.True(nameOffset >= 0);
        var typeOffset = nameOffset + name.Length;
        Assert.True(payload.Slice(typeOffset, type.Length).SequenceEqual(type));
        var cursor = typeOffset + type.Length + sizeof(ulong);
        Assert.Equal(0, payload[cursor]);
        cursor++;
        return DecodeFString(payload, cursor);
    }

    private static byte[] Wrap(ReadOnlySpan<byte> payload, byte saveType)
    {
        var inner = Compress(payload);
        var final = saveType == 0x32 ? Compress(inner) : inner;
        var result = new byte[12 + final.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(0, 4), checked((uint)payload.Length));
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(4, 4), checked((uint)inner.Length));
        "PlZ"u8.CopyTo(result.AsSpan(8, 3));
        result[11] = saveType;
        final.CopyTo(result, 12);
        return result;
    }

    private static byte[] Decode(ReadOnlySpan<byte> save)
    {
        var payload = save[12..];
        var first = Decompress(payload);
        return save[11] == 0x32 ? Decompress(first) : first;
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

    private static byte[] Decompress(ReadOnlySpan<byte> payload)
    {
        using var source = new MemoryStream(payload.ToArray(), writable: false);
        using var zlib = new ZLibStream(source, CompressionMode.Decompress);
        using var destination = new MemoryStream();
        zlib.CopyTo(destination);
        return destination.ToArray();
    }

    private static byte[] EncodeFString(string value)
    {
        if (value.Length == 0)
        {
            return new byte[sizeof(int)];
        }

        if (value.All(character => character <= 0x7F))
        {
            var text = Encoding.UTF8.GetBytes(value);
            var result = new byte[sizeof(int) + text.Length + 1];
            BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(0, sizeof(int)), text.Length + 1);
            text.CopyTo(result, sizeof(int));
            return result;
        }

        var utf16 = Encoding.Unicode.GetBytes(value);
        var resultUtf16 = new byte[sizeof(int) + utf16.Length + sizeof(char)];
        BinaryPrimitives.WriteInt32LittleEndian(resultUtf16.AsSpan(0, sizeof(int)), -(value.Length + 1));
        utf16.CopyTo(resultUtf16, sizeof(int));
        return resultUtf16;
    }

    private static string DecodeFString(ReadOnlySpan<byte> payload, int offset)
    {
        var length = BinaryPrimitives.ReadInt32LittleEndian(payload.Slice(offset, sizeof(int)));
        if (length == 0)
        {
            return string.Empty;
        }

        return length > 0
            ? Encoding.UTF8.GetString(payload.Slice(offset + sizeof(int), length - 1))
            : Encoding.Unicode.GetString(payload.Slice(offset + sizeof(int), (-length * 2) - 2));
    }
}
