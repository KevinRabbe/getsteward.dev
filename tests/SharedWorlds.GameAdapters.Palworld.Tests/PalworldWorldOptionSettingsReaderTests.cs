using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace SharedWorlds.GameAdapters.Palworld.Tests;

public sealed class PalworldWorldOptionSettingsReaderTests
{
    [Fact]
    public void ReadsKnownSettingsFromLegacyPlZWithoutChangingBytes()
    {
        var payload = BuildGvasPayload();
        var save = WrapPlZ(payload);
        var before = save.ToArray();

        var snapshot = PalworldWorldOptionSettingsReader.Read(save);

        Assert.Equal("PlZ/0x31", snapshot.Container);
        Assert.Equal(before, save);

        var settings = snapshot.Settings.ToDictionary(setting => setting.Name, StringComparer.Ordinal);
        Assert.Equal("1.25", settings["ExpRate"].Value);
        Assert.Equal("FloatProperty", settings["ExpRate"].PropertyType);
        Assert.Equal("20", settings["BaseCampWorkerMaxNum"].Value);
        Assert.Equal("True", settings["bIsPvP"].Value);
        Assert.Equal("Steward Test", settings["ServerName"].Value);
        Assert.Equal("EPalOptionWorldDeathPenalty::ItemAndEquipment", settings["DeathPenalty"].Value);
        Assert.Equal("EPalOptionWorldDeathPenalty", settings["DeathPenalty"].ValueType);
        Assert.True(settings["AdminPassword"].IsSensitive);
        Assert.Equal("do-not-print-me", settings["AdminPassword"].Value);
    }

    [Fact]
    public void CurrentPlMUsesOodleOnlyForReadOnlyDecode()
    {
        var codec = new TestOodleCodec();
        var payload = BuildGvasPayload();
        var save = WrapPlM(payload, codec);
        var compressCallsBeforeRead = codec.CompressCalls;

        var snapshot = PalworldWorldOptionSettingsReader.Read(save, codec);

        Assert.Equal("PlM/0x31", snapshot.Container);
        Assert.Equal(compressCallsBeforeRead, codec.CompressCalls);
        Assert.True(codec.DecompressCalls > 0);
        Assert.Contains(snapshot.Settings, setting => setting.Name == "BaseCampWorkerMaxNum" && setting.Value == "20");
    }

    [Fact]
    public void PlMWithoutDecoderFailsClosed()
    {
        var codec = new TestOodleCodec();
        var save = WrapPlM(BuildGvasPayload(), codec);

        var exception = Assert.Throws<InvalidDataException>(() =>
            PalworldWorldOptionSettingsReader.Read(save));

        Assert.Contains("Oodle", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MultipleStructurallyValidOptionWorldDataPropertiesFailClosed()
    {
        var optionWorldData = BuildOptionWorldDataProperty();
        var payload = new List<byte>("GVAS"u8.ToArray());
        payload.AddRange(optionWorldData);
        payload.AddRange(optionWorldData);
        var save = WrapPlZ(payload.ToArray());

        var exception = Assert.Throws<InvalidDataException>(() =>
            PalworldWorldOptionSettingsReader.Read(save));

        Assert.Contains("multiple", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MissingNestedNoneTerminatorFailsClosed()
    {
        var settingsValue = BuildSettingsValue(includeTerminator: false);
        var optionValue = new List<byte>();
        optionValue.AddRange(StructProperty("Settings", "PalOptionWorldSettings", settingsValue));
        optionValue.AddRange(EncodeFString("None"));

        var payload = new List<byte>("GVAS"u8.ToArray());
        payload.AddRange(StructProperty("OptionWorldData", "PalOptionWorldSaveData", optionValue.ToArray()));
        var save = WrapPlZ(payload.ToArray());

        var exception = Assert.Throws<InvalidDataException>(() =>
            PalworldWorldOptionSettingsReader.Read(save));

        Assert.Contains("terminator", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static byte[] BuildGvasPayload()
    {
        var payload = new List<byte>("GVAS"u8.ToArray());
        payload.AddRange([0x01, 0x02, 0x03, 0x04]);
        payload.AddRange(BuildOptionWorldDataProperty());
        return payload.ToArray();
    }

    private static byte[] BuildOptionWorldDataProperty()
    {
        var optionValue = new List<byte>();
        optionValue.AddRange(StructProperty(
            "Settings",
            "PalOptionWorldSettings",
            BuildSettingsValue(includeTerminator: true)));
        optionValue.AddRange(EncodeFString("None"));
        return StructProperty("OptionWorldData", "PalOptionWorldSaveData", optionValue.ToArray());
    }

    private static byte[] BuildSettingsValue(bool includeTerminator)
    {
        var settings = new List<byte>();
        settings.AddRange(FloatProperty("ExpRate", 1.25f));
        settings.AddRange(IntProperty("BaseCampWorkerMaxNum", 20));
        settings.AddRange(BoolProperty("bIsPvP", true));
        settings.AddRange(StrProperty("ServerName", "Steward Test"));
        settings.AddRange(StrProperty("AdminPassword", "do-not-print-me"));
        settings.AddRange(EnumProperty(
            "DeathPenalty",
            "EPalOptionWorldDeathPenalty",
            "EPalOptionWorldDeathPenalty::ItemAndEquipment"));
        if (includeTerminator)
        {
            settings.AddRange(EncodeFString("None"));
        }

        return settings.ToArray();
    }

    private static byte[] StructProperty(string name, string structType, byte[] value)
    {
        var result = new List<byte>();
        result.AddRange(EncodeFString(name));
        result.AddRange(EncodeFString("StructProperty"));
        result.AddRange(UInt64Bytes((ulong)value.Length));
        result.AddRange(EncodeFString(structType));
        result.AddRange(new byte[16]);
        result.Add(0);
        result.AddRange(value);
        return result.ToArray();
    }

    private static byte[] IntProperty(string name, int value)
    {
        var bytes = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, value);
        return ScalarProperty(name, "IntProperty", bytes);
    }

    private static byte[] FloatProperty(string name, float value)
    {
        var bytes = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, BitConverter.SingleToInt32Bits(value));
        return ScalarProperty(name, "FloatProperty", bytes);
    }

    private static byte[] StrProperty(string name, string value)
        => ScalarProperty(name, "StrProperty", EncodeFString(value));

    private static byte[] BoolProperty(string name, bool value)
    {
        var result = new List<byte>();
        result.AddRange(EncodeFString(name));
        result.AddRange(EncodeFString("BoolProperty"));
        result.AddRange(UInt64Bytes(0));
        result.Add(value ? (byte)1 : (byte)0);
        result.Add(0);
        return result.ToArray();
    }

    private static byte[] EnumProperty(string name, string enumType, string value)
    {
        var encodedValue = EncodeFString(value);
        var result = new List<byte>();
        result.AddRange(EncodeFString(name));
        result.AddRange(EncodeFString("EnumProperty"));
        result.AddRange(UInt64Bytes((ulong)encodedValue.Length));
        result.AddRange(EncodeFString(enumType));
        result.Add(0);
        result.AddRange(encodedValue);
        return result.ToArray();
    }

    private static byte[] ScalarProperty(string name, string type, byte[] value)
    {
        var result = new List<byte>();
        result.AddRange(EncodeFString(name));
        result.AddRange(EncodeFString(type));
        result.AddRange(UInt64Bytes((ulong)value.Length));
        result.Add(0);
        result.AddRange(value);
        return result.ToArray();
    }

    private static byte[] WrapPlZ(ReadOnlySpan<byte> payload)
    {
        var compressed = Compress(payload);
        return Wrap(payload.Length, compressed, "PlZ"u8, 0x31);
    }

    private static byte[] WrapPlM(ReadOnlySpan<byte> payload, IPalworldOodleCodec codec)
    {
        var compressed = codec.CompressMermaid(payload);
        return Wrap(payload.Length, compressed, "PlM"u8, 0x31);
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

    private static byte[] UInt64Bytes(ulong value)
    {
        var bytes = new byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(bytes, value);
        return bytes;
    }

    private static byte[] EncodeFString(string value)
    {
        var text = Encoding.UTF8.GetBytes(value);
        var result = new byte[4 + text.Length + 1];
        BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(0, 4), text.Length + 1);
        text.CopyTo(result, 4);
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
        public int CompressCalls { get; private set; }

        public int DecompressCalls { get; private set; }

        public byte[] Decompress(ReadOnlySpan<byte> compressed, int uncompressedLength)
        {
            DecompressCalls++;
            var result = PalworldWorldOptionSettingsReaderTests.Decompress(compressed);
            Assert.Equal(uncompressedLength, result.Length);
            return result;
        }

        public byte[] CompressMermaid(ReadOnlySpan<byte> payload)
        {
            CompressCalls++;
            return Compress(payload);
        }
    }
}
