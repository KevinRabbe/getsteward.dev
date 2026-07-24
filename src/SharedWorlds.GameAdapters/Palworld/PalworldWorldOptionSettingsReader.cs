using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace SharedWorlds.GameAdapters.Palworld;

internal sealed record PalworldWorldOptionSetting(
    string Name,
    string PropertyType,
    string? Value,
    string? ValueType,
    int SerializedValueBytes,
    bool IsSensitive);

internal sealed record PalworldWorldOptionSettingsSnapshot(
    string Container,
    IReadOnlyList<PalworldWorldOptionSetting> Settings);

/// <summary>
/// Read-only reader for Palworld's WorldOption.sav settings payload.
/// It never writes or re-encodes save data. Current PlM input is decoded through the
/// supplied Oodle codec; legacy PlZ input is decoded with zlib. The parser then finds
/// the unique OptionWorldData -> Settings tagged-property path and reports the contained
/// setting names/types. Unknown values are preserved as opaque metadata rather than guessed.
/// </summary>
internal static class PalworldWorldOptionSettingsReader
{
    private const int WrapperHeaderLength = 12;
    private const byte SingleCompressionSaveType = 0x31;
    private const byte DoubleZlibSaveType = 0x32;

    private static readonly byte[] PlMMagic = "PlM"u8.ToArray();
    private static readonly byte[] PlZMagic = "PlZ"u8.ToArray();
    private static readonly byte[] GvasMagic = "GVAS"u8.ToArray();
    private static readonly byte[] OptionWorldDataName = EncodeFString("OptionWorldData");
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static bool RequiresOodle(ReadOnlySpan<byte> save)
        => save.Length >= WrapperHeaderLength && save.Slice(8, 3).SequenceEqual(PlMMagic);

    public static PalworldWorldOptionSettingsSnapshot Read(
        ReadOnlySpan<byte> save,
        IPalworldOodleCodec? oodleCodec = null)
    {
        var decoded = Decode(save, oodleCodec);
        var optionWorldData = FindUniqueStructProperty(
            decoded.Payload,
            OptionWorldDataName,
            expectedStructType: "PalOptionWorldSaveData");

        var optionProperties = ReadPropertyList(optionWorldData.ValueBytes, "OptionWorldData");
        var matchingSettings = optionProperties
            .Where(property => string.Equals(property.Name, "Settings", StringComparison.Ordinal))
            .ToArray();
        if (matchingSettings.Length != 1)
        {
            throw new InvalidDataException(
                $"WorldOption.sav OptionWorldData must contain exactly one Settings property; found {matchingSettings.Length}.");
        }

        var settings = matchingSettings[0];
        if (!string.Equals(settings.PropertyType, "StructProperty", StringComparison.Ordinal) ||
            !string.Equals(settings.ValueType, "PalOptionWorldSettings", StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"WorldOption.sav Settings has unexpected type {settings.PropertyType}/{settings.ValueType ?? "(none)"}.");
        }

        var settingsProperties = ReadPropertyList(settings.ValueBytes, "OptionWorldData.Settings");
        return new PalworldWorldOptionSettingsSnapshot(
            decoded.Container,
            settingsProperties
                .Select(property => new PalworldWorldOptionSetting(
                    property.Name,
                    property.PropertyType,
                    property.DisplayValue,
                    property.ValueType,
                    property.ValueBytes.Length,
                    property.Name.Contains("Password", StringComparison.OrdinalIgnoreCase)))
                .ToArray());
    }

    private static DecodedWorldOption Decode(
        ReadOnlySpan<byte> save,
        IPalworldOodleCodec? oodleCodec)
    {
        if (save.Length < WrapperHeaderLength)
        {
            throw new InvalidDataException("WorldOption.sav is too small to contain a Palworld wrapper.");
        }

        var uncompressedLength = ReadLength32(save[..4], "uncompressed");
        var compressedLength = ReadLength32(save.Slice(4, 4), "compressed");
        if (save.Length != WrapperHeaderLength + compressedLength)
        {
            throw new InvalidDataException(
                $"WorldOption.sav wrapper length mismatch: header declares {compressedLength} compressed bytes but file contains {save.Length - WrapperHeaderLength}.");
        }

        var magic = save.Slice(8, 3);
        var saveType = save[11];
        var compressed = save[WrapperHeaderLength..];
        byte[] payload;
        string container;

        if (magic.SequenceEqual(PlMMagic))
        {
            if (saveType != SingleCompressionSaveType)
            {
                throw new InvalidDataException(
                    $"WorldOption.sav PlM wrapper has unsupported save type 0x{saveType:X2}.");
            }

            if (oodleCodec is null)
            {
                throw new InvalidDataException(
                    "WorldOption.sav uses the current PlM/Oodle container, but no Oodle decoder was supplied.");
            }

            payload = oodleCodec.Decompress(compressed, uncompressedLength);
            container = $"PlM/0x{saveType:X2}";
        }
        else if (magic.SequenceEqual(PlZMagic))
        {
            payload = saveType switch
            {
                SingleCompressionSaveType => DecompressZlib(compressed, uncompressedLength),
                DoubleZlibSaveType => DecompressDoubleZlib(compressed, uncompressedLength),
                _ => throw new InvalidDataException(
                    $"WorldOption.sav PlZ wrapper has unsupported save type 0x{saveType:X2}.")
            };
            container = $"PlZ/0x{saveType:X2}";
        }
        else
        {
            throw new InvalidDataException(
                $"WorldOption.sav uses unknown wrapper magic {Convert.ToHexString(magic)}.");
        }

        if (!payload.AsSpan().StartsWith(GvasMagic))
        {
            throw new InvalidDataException("Decoded WorldOption.sav payload does not begin with GVAS.");
        }

        return new DecodedWorldOption(container, payload);
    }

    private static int ReadLength32(ReadOnlySpan<byte> bytes, string description)
    {
        var value = BinaryPrimitives.ReadUInt32LittleEndian(bytes);
        if (value is 0 or > int.MaxValue)
        {
            throw new InvalidDataException(
                $"WorldOption.sav declares an invalid {description} length {value}.");
        }

        return checked((int)value);
    }

    private static byte[] DecompressZlib(ReadOnlySpan<byte> compressed, int expectedLength)
    {
        using var source = new MemoryStream(compressed.ToArray(), writable: false);
        using var zlib = new ZLibStream(source, CompressionMode.Decompress);
        using var destination = new MemoryStream(expectedLength);
        zlib.CopyTo(destination);
        var bytes = destination.ToArray();
        if (bytes.Length != expectedLength)
        {
            throw new InvalidDataException(
                $"WorldOption.sav zlib payload decoded to {bytes.Length} bytes; expected {expectedLength}.");
        }

        return bytes;
    }

    private static byte[] DecompressDoubleZlib(ReadOnlySpan<byte> compressed, int expectedLength)
    {
        using var source = new MemoryStream(compressed.ToArray(), writable: false);
        using var outer = new ZLibStream(source, CompressionMode.Decompress);
        using var middle = new MemoryStream();
        outer.CopyTo(middle);
        return DecompressZlib(middle.ToArray(), expectedLength);
    }

    private static TaggedProperty FindUniqueStructProperty(
        byte[] payload,
        byte[] encodedName,
        string expectedStructType)
    {
        var matches = new List<TaggedProperty>();
        var searchOffset = 0;
        while (searchOffset <= payload.Length - encodedName.Length)
        {
            var relativeIndex = payload.AsSpan(searchOffset).IndexOf(encodedName);
            if (relativeIndex < 0)
            {
                break;
            }

            var absoluteIndex = searchOffset + relativeIndex;
            try
            {
                var property = ReadProperty(payload, absoluteIndex, "GVAS");
                if (string.Equals(property.Name, DecodeFString(encodedName), StringComparison.Ordinal) &&
                    string.Equals(property.PropertyType, "StructProperty", StringComparison.Ordinal) &&
                    string.Equals(property.ValueType, expectedStructType, StringComparison.Ordinal))
                {
                    matches.Add(property);
                }
            }
            catch (InvalidDataException)
            {
                // This byte sequence was not the start of the tagged property we need.
            }

            searchOffset = absoluteIndex + 1;
        }

        return matches.Count switch
        {
            1 => matches[0],
            0 => throw new InvalidDataException(
                $"WorldOption.sav does not contain a structurally valid {DecodeFString(encodedName)} StructProperty/{expectedStructType}."),
            _ => throw new InvalidDataException(
                $"WorldOption.sav contains multiple structurally valid {DecodeFString(encodedName)} StructProperty/{expectedStructType} candidates.")
        };
    }

    private static IReadOnlyList<TaggedProperty> ReadPropertyList(byte[] bytes, string context)
    {
        var properties = new List<TaggedProperty>();
        var offset = 0;
        while (offset < bytes.Length)
        {
            var (name, nextOffset) = ReadFString(bytes, offset, $"{context} property name");
            if (string.Equals(name, "None", StringComparison.Ordinal))
            {
                if (nextOffset != bytes.Length)
                {
                    throw new InvalidDataException(
                        $"{context} contains trailing bytes after its None terminator.");
                }

                return properties;
            }

            var property = ReadProperty(bytes, offset, context);
            properties.Add(property);
            offset = property.NextOffset;
        }

        throw new InvalidDataException($"{context} is missing the required None property terminator.");
    }

    private static TaggedProperty ReadProperty(byte[] bytes, int offset, string context)
    {
        var start = offset;
        var (name, afterName) = ReadFString(bytes, offset, $"{context} property name");
        if (string.Equals(name, "None", StringComparison.Ordinal))
        {
            throw new InvalidDataException($"{context} attempted to read None as a tagged property.");
        }

        var (propertyType, afterType) = ReadFString(bytes, afterName, $"{context}.{name} property type");
        EnsureAvailable(bytes, afterType, sizeof(ulong), $"{context}.{name} value size");
        var valueSize = BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(afterType, sizeof(ulong)));
        if (valueSize > int.MaxValue)
        {
            throw new InvalidDataException($"{context}.{name} value is too large.");
        }

        offset = afterType + sizeof(ulong);
        string? valueType = null;
        switch (propertyType)
        {
            case "StructProperty":
                (valueType, offset) = ReadFString(bytes, offset, $"{context}.{name} struct type");
                EnsureAvailable(bytes, offset, 16, $"{context}.{name} struct GUID");
                offset += 16;
                break;
            case "EnumProperty":
            case "ByteProperty":
            case "ArrayProperty":
            case "SetProperty":
                (valueType, offset) = ReadFString(bytes, offset, $"{context}.{name} value type");
                break;
            case "MapProperty":
                var (keyType, afterKeyType) = ReadFString(bytes, offset, $"{context}.{name} map key type");
                var (mapValueType, afterMapValueType) = ReadFString(bytes, afterKeyType, $"{context}.{name} map value type");
                valueType = $"{keyType}->{mapValueType}";
                offset = afterMapValueType;
                break;
        }

        EnsureAvailable(bytes, offset, 1, $"{context}.{name} property GUID flag");
        var hasPropertyGuid = bytes[offset++] != 0;
        if (hasPropertyGuid)
        {
            EnsureAvailable(bytes, offset, 16, $"{context}.{name} property GUID");
            offset += 16;
        }

        if (string.Equals(propertyType, "BoolProperty", StringComparison.Ordinal))
        {
            // Unreal stores the Boolean value in the tag before the optional GUID flag. The field's
            // serialized value size is therefore zero.
            var boolOffset = offset - 1 - (hasPropertyGuid ? 16 : 0) - 1;
            if (boolOffset < start)
            {
                throw new InvalidDataException($"{context}.{name} Boolean tag is malformed.");
            }

            var boolValue = bytes[boolOffset] != 0;
            return new TaggedProperty(
                name,
                propertyType,
                valueType,
                Array.Empty<byte>(),
                boolValue ? "True" : "False",
                offset);
        }

        var valueLength = checked((int)valueSize);
        EnsureAvailable(bytes, offset, valueLength, $"{context}.{name} value bytes");
        var valueBytes = bytes.AsSpan(offset, valueLength).ToArray();
        var displayValue = TryFormatScalarValue(propertyType, valueType, valueBytes);
        return new TaggedProperty(
            name,
            propertyType,
            valueType,
            valueBytes,
            displayValue,
            checked(offset + valueLength));
    }

    private static string? TryFormatScalarValue(
        string propertyType,
        string? valueType,
        byte[] valueBytes)
    {
        return propertyType switch
        {
            "IntProperty" when valueBytes.Length == sizeof(int) =>
                BinaryPrimitives.ReadInt32LittleEndian(valueBytes).ToString(System.Globalization.CultureInfo.InvariantCulture),
            "Int64Property" when valueBytes.Length == sizeof(long) =>
                BinaryPrimitives.ReadInt64LittleEndian(valueBytes).ToString(System.Globalization.CultureInfo.InvariantCulture),
            "UInt32Property" when valueBytes.Length == sizeof(uint) =>
                BinaryPrimitives.ReadUInt32LittleEndian(valueBytes).ToString(System.Globalization.CultureInfo.InvariantCulture),
            "UInt64Property" when valueBytes.Length == sizeof(ulong) =>
                BinaryPrimitives.ReadUInt64LittleEndian(valueBytes).ToString(System.Globalization.CultureInfo.InvariantCulture),
            "FloatProperty" when valueBytes.Length == sizeof(float) =>
                BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(valueBytes))
                    .ToString("R", System.Globalization.CultureInfo.InvariantCulture),
            "DoubleProperty" when valueBytes.Length == sizeof(double) =>
                BitConverter.Int64BitsToDouble(BinaryPrimitives.ReadInt64LittleEndian(valueBytes))
                    .ToString("R", System.Globalization.CultureInfo.InvariantCulture),
            "StrProperty" or "NameProperty" => TryDecodeSingleFString(valueBytes),
            "EnumProperty" or "ByteProperty" => TryDecodeSingleFString(valueBytes),
            "ArrayProperty" when valueType is "EnumProperty" or "NameProperty" or "StrProperty" =>
                TryDecodeSimpleStringArray(valueBytes),
            _ => null
        };
    }

    private static string? TryDecodeSimpleStringArray(byte[] valueBytes)
    {
        if (valueBytes.Length < sizeof(uint))
        {
            return null;
        }

        try
        {
            var count = BinaryPrimitives.ReadUInt32LittleEndian(valueBytes);
            if (count > int.MaxValue)
            {
                return null;
            }

            var values = new string[checked((int)count)];
            var offset = sizeof(uint);
            for (var index = 0; index < values.Length; index++)
            {
                (values[index], offset) = ReadFString(
                    valueBytes,
                    offset,
                    $"simple array element {index}");
            }

            if (offset != valueBytes.Length)
            {
                return null;
            }

            return $"({string.Join(',', values)})";
        }
        catch (InvalidDataException)
        {
            return null;
        }
    }

    private static string? TryDecodeSingleFString(byte[] bytes)
    {
        try
        {
            var (value, nextOffset) = ReadFString(bytes, 0, "scalar FString");
            return nextOffset == bytes.Length ? value : null;
        }
        catch (InvalidDataException)
        {
            return null;
        }
    }

    private static string DecodeFString(byte[] encoded)
    {
        var (value, nextOffset) = ReadFString(encoded, 0, "encoded FString");
        if (nextOffset != encoded.Length)
        {
            throw new InvalidDataException("Encoded FString contains trailing bytes.");
        }

        return value;
    }

    private static (string Value, int NextOffset) ReadFString(
        byte[] bytes,
        int offset,
        string context)
    {
        EnsureAvailable(bytes, offset, sizeof(int), context);
        var length = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(offset, sizeof(int)));
        offset += sizeof(int);
        if (length == 0)
        {
            return (string.Empty, offset);
        }

        if (length > 0)
        {
            EnsureAvailable(bytes, offset, length, context);
            var valueBytes = bytes.AsSpan(offset, length);
            if (valueBytes[^1] != 0)
            {
                throw new InvalidDataException($"{context} ANSI FString is missing its null terminator.");
            }

            return (
                StrictUtf8.GetString(valueBytes[..^1]),
                checked(offset + length));
        }

        var characterCount = checked(-length);
        var byteCount = checked(characterCount * sizeof(char));
        EnsureAvailable(bytes, offset, byteCount, context);
        var valueBytesUtf16 = bytes.AsSpan(offset, byteCount);
        if (valueBytesUtf16[^2] != 0 || valueBytesUtf16[^1] != 0)
        {
            throw new InvalidDataException($"{context} UTF-16 FString is missing its null terminator.");
        }

        return (
            Encoding.Unicode.GetString(valueBytesUtf16[..^2]),
            checked(offset + byteCount));
    }

    private static void EnsureAvailable(byte[] bytes, int offset, int length, string context)
    {
        if (offset < 0 || length < 0 || offset > bytes.Length - length)
        {
            throw new InvalidDataException($"{context} exceeds the available bytes.");
        }
    }

    private static byte[] EncodeFString(string value)
    {
        var text = Encoding.UTF8.GetBytes(value);
        var result = new byte[sizeof(int) + text.Length + 1];
        BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(0, sizeof(int)), text.Length + 1);
        text.CopyTo(result.AsSpan(sizeof(int)));
        return result;
    }

    private sealed record DecodedWorldOption(string Container, byte[] Payload);

    private sealed record TaggedProperty(
        string Name,
        string PropertyType,
        string? ValueType,
        byte[] ValueBytes,
        string? DisplayValue,
        int NextOffset);
}
