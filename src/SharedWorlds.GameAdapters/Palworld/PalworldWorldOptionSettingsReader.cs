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
/// Read-only acceptance reader for Palworld's WorldOption.sav settings payload.
/// It never writes or re-encodes save data. Current PlM input is decoded through the
/// acceptance Oodle codec; legacy PlZ input is decoded with zlib. The parser then finds
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
        var magic = save.Slice(8, 3);
        var saveType = save[11];
        var compressedPayload = save[WrapperHeaderLength..];

        byte[] payload;
        string container;
        if (magic.SequenceEqual(PlMMagic))
        {
            if (saveType != SingleCompressionSaveType)
            {
                throw new InvalidDataException($"Unsupported Palworld PlM save type 0x{saveType:X2}.");
            }

            if (compressedLength != compressedPayload.Length)
            {
                throw new InvalidDataException("WorldOption.sav PlM compressed length is inconsistent.");
            }

            if (oodleCodec is null)
            {
                throw new InvalidDataException("WorldOption.sav PlM input requires an Oodle decoder for this acceptance read.");
            }

            payload = oodleCodec.Decompress(compressedPayload, uncompressedLength);
            container = $"PlM/0x{saveType:X2}";
        }
        else if (magic.SequenceEqual(PlZMagic))
        {
            if (saveType == SingleCompressionSaveType)
            {
                if (compressedLength != compressedPayload.Length)
                {
                    throw new InvalidDataException("WorldOption.sav PlZ compressed length is inconsistent.");
                }

                payload = DecompressZlib(compressedPayload);
            }
            else if (saveType == DoubleZlibSaveType)
            {
                var innerCompressed = DecompressZlib(compressedPayload);
                if (compressedLength != innerCompressed.Length)
                {
                    throw new InvalidDataException("WorldOption.sav PlZ inner compressed length is inconsistent.");
                }

                payload = DecompressZlib(innerCompressed);
            }
            else
            {
                throw new InvalidDataException($"Unsupported Palworld PlZ save type 0x{saveType:X2}.");
            }

            container = $"PlZ/0x{saveType:X2}";
        }
        else
        {
            throw new InvalidDataException(
                $"WorldOption.sav uses unsupported wrapper magic {Convert.ToHexString(magic)}.");
        }

        if (payload.Length != uncompressedLength)
        {
            throw new InvalidDataException("WorldOption.sav uncompressed length is inconsistent.");
        }

        if (payload.Length < GvasMagic.Length || !payload.AsSpan(0, GvasMagic.Length).SequenceEqual(GvasMagic))
        {
            throw new InvalidDataException("Decoded WorldOption.sav does not begin with GVAS.");
        }

        return new DecodedWorldOption(container, payload);
    }

    private static int ReadLength32(ReadOnlySpan<byte> bytes, string description)
    {
        var value = BinaryPrimitives.ReadUInt32LittleEndian(bytes);
        if (value > int.MaxValue)
        {
            throw new InvalidDataException($"WorldOption.sav {description} length exceeds the supported acceptance bound.");
        }

        return (int)value;
    }

    private static ParsedProperty FindUniqueStructProperty(
        ReadOnlySpan<byte> payload,
        ReadOnlySpan<byte> encodedName,
        string expectedStructType)
    {
        ParsedProperty? match = null;
        var searchOffset = 0;
        while (searchOffset <= payload.Length - encodedName.Length)
        {
            var relative = payload[searchOffset..].IndexOf(encodedName);
            if (relative < 0)
            {
                break;
            }

            var offset = searchOffset + relative;
            searchOffset = offset + encodedName.Length;

            var property = TryReadCandidate(payload, offset);
            if (property is null ||
                !string.Equals(property.Name, "OptionWorldData", StringComparison.Ordinal) ||
                !string.Equals(property.PropertyType, "StructProperty", StringComparison.Ordinal) ||
                !string.Equals(property.ValueType, expectedStructType, StringComparison.Ordinal))
            {
                continue;
            }

            if (match is not null)
            {
                throw new InvalidDataException(
                    "WorldOption.sav contains multiple structurally valid OptionWorldData properties.");
            }

            match = property;
        }

        return match ?? throw new InvalidDataException(
            "WorldOption.sav does not contain one structurally valid OptionWorldData PalOptionWorldSaveData property.");
    }

    private static ParsedProperty? TryReadCandidate(ReadOnlySpan<byte> payload, int offset)
    {
        try
        {
            var cursor = offset;
            return ReadProperty(payload, ref cursor, "root-search");
        }
        catch (InvalidDataException)
        {
            return null;
        }
        catch (OverflowException)
        {
            return null;
        }
    }

    private static IReadOnlyList<ParsedProperty> ReadPropertyList(
        ReadOnlyMemory<byte> memory,
        string path)
    {
        var data = memory.Span;
        var result = new List<ParsedProperty>();
        var cursor = 0;
        var terminated = false;
        while (cursor < data.Length)
        {
            var before = cursor;
            var property = ReadProperty(data, ref cursor, path);
            if (property is null)
            {
                terminated = true;
                break;
            }

            result.Add(property);
            if (cursor <= before)
            {
                throw new InvalidDataException($"WorldOption.sav parser made no progress at {path}.");
            }
        }

        if (!terminated)
        {
            throw new InvalidDataException($"WorldOption.sav {path} property list is missing its None terminator.");
        }

        if (cursor != data.Length)
        {
            throw new InvalidDataException($"WorldOption.sav {path} contains unexpected trailing bytes after its None terminator.");
        }

        return result;
    }

    private static ParsedProperty? ReadProperty(
        ReadOnlySpan<byte> data,
        ref int cursor,
        string path)
    {
        var name = ReadFString(data, ref cursor);
        if (string.Equals(name, "None", StringComparison.Ordinal))
        {
            return null;
        }

        var propertyType = ReadFString(data, ref cursor);
        EnsureRemaining(data, cursor, sizeof(ulong), path, name);
        var declaredSize64 = BinaryPrimitives.ReadUInt64LittleEndian(data.Slice(cursor, sizeof(ulong)));
        cursor += sizeof(ulong);
        if (declaredSize64 > int.MaxValue)
        {
            throw new InvalidDataException($"WorldOption.sav {path}.{name} exceeds the supported acceptance value bound.");
        }

        var declaredSize = (int)declaredSize64;
        string? valueType = null;
        string? displayValue = null;
        ReadOnlyMemory<byte> valueBytes;

        if (propertyType == "BoolProperty")
        {
            EnsureRemaining(data, cursor, 1, path, name);
            var value = data[cursor++];
            if (value is not (0 or 1))
            {
                throw new InvalidDataException($"WorldOption.sav {path}.{name} has invalid BoolProperty value {value}.");
            }

            ReadPropertyGuid(data, ref cursor, path, name);
            if (declaredSize != 0)
            {
                throw new InvalidDataException($"WorldOption.sav {path}.{name} BoolProperty declared non-zero size {declaredSize}.");
            }

            valueBytes = ReadOnlyMemory<byte>.Empty;
            displayValue = value == 1 ? "True" : "False";
        }
        else if (propertyType == "StructProperty")
        {
            valueType = ReadFString(data, ref cursor);
            EnsureRemaining(data, cursor, 16, path, name);
            cursor += 16;
            ReadPropertyGuid(data, ref cursor, path, name);
            valueBytes = ReadValueBytes(data, ref cursor, declaredSize, path, name);
        }
        else if (propertyType == "EnumProperty")
        {
            valueType = ReadFString(data, ref cursor);
            ReadPropertyGuid(data, ref cursor, path, name);
            valueBytes = ReadValueBytes(data, ref cursor, declaredSize, path, name);
            displayValue = TryReadSingleFString(valueBytes.Span);
        }
        else if (propertyType == "ByteProperty")
        {
            valueType = ReadFString(data, ref cursor);
            ReadPropertyGuid(data, ref cursor, path, name);
            valueBytes = ReadValueBytes(data, ref cursor, declaredSize, path, name);
            displayValue = declaredSize == 1
                ? valueBytes.Span[0].ToString(System.Globalization.CultureInfo.InvariantCulture)
                : TryReadSingleFString(valueBytes.Span);
        }
        else if (propertyType is "ArrayProperty" or "SetProperty")
        {
            valueType = ReadFString(data, ref cursor);
            ReadPropertyGuid(data, ref cursor, path, name);
            valueBytes = ReadValueBytes(data, ref cursor, declaredSize, path, name);
        }
        else if (propertyType == "MapProperty")
        {
            var keyType = ReadFString(data, ref cursor);
            var elementType = ReadFString(data, ref cursor);
            valueType = $"{keyType}->{elementType}";
            ReadPropertyGuid(data, ref cursor, path, name);
            valueBytes = ReadValueBytes(data, ref cursor, declaredSize, path, name);
        }
        else
        {
            ReadPropertyGuid(data, ref cursor, path, name);
            valueBytes = ReadValueBytes(data, ref cursor, declaredSize, path, name);
            displayValue = TryFormatScalar(propertyType, valueBytes.Span);
        }

        return new ParsedProperty(name, propertyType, valueType, displayValue, valueBytes);
    }

    private static string? TryFormatScalar(string propertyType, ReadOnlySpan<byte> value)
    {
        return propertyType switch
        {
            "IntProperty" when value.Length == 4 => BinaryPrimitives.ReadInt32LittleEndian(value)
                .ToString(System.Globalization.CultureInfo.InvariantCulture),
            "Int64Property" when value.Length == 8 => BinaryPrimitives.ReadInt64LittleEndian(value)
                .ToString(System.Globalization.CultureInfo.InvariantCulture),
            "UInt32Property" when value.Length == 4 => BinaryPrimitives.ReadUInt32LittleEndian(value)
                .ToString(System.Globalization.CultureInfo.InvariantCulture),
            "UInt64Property" when value.Length == 8 => BinaryPrimitives.ReadUInt64LittleEndian(value)
                .ToString(System.Globalization.CultureInfo.InvariantCulture),
            "FloatProperty" when value.Length == 4 => BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(value))
                .ToString("R", System.Globalization.CultureInfo.InvariantCulture),
            "DoubleProperty" when value.Length == 8 => BitConverter.Int64BitsToDouble(BinaryPrimitives.ReadInt64LittleEndian(value))
                .ToString("R", System.Globalization.CultureInfo.InvariantCulture),
            "StrProperty" => TryReadSingleFString(value),
            "NameProperty" => TryReadSingleFString(value),
            _ => null
        };
    }

    private static string? TryReadSingleFString(ReadOnlySpan<byte> value)
    {
        try
        {
            var cursor = 0;
            var text = ReadFString(value, ref cursor);
            return cursor == value.Length ? text : null;
        }
        catch (InvalidDataException)
        {
            return null;
        }
        catch (OverflowException)
        {
            return null;
        }
    }

    private static void ReadPropertyGuid(
        ReadOnlySpan<byte> data,
        ref int cursor,
        string path,
        string name)
    {
        EnsureRemaining(data, cursor, 1, path, name);
        var hasGuid = data[cursor++];
        if (hasGuid == 0)
        {
            return;
        }

        if (hasGuid != 1)
        {
            throw new InvalidDataException($"WorldOption.sav {path}.{name} has invalid property GUID flag {hasGuid}.");
        }

        EnsureRemaining(data, cursor, 16, path, name);
        cursor += 16;
    }

    private static ReadOnlyMemory<byte> ReadValueBytes(
        ReadOnlySpan<byte> data,
        ref int cursor,
        int length,
        string path,
        string name)
    {
        EnsureRemaining(data, cursor, length, path, name);
        var bytes = data.Slice(cursor, length).ToArray();
        cursor += length;
        return bytes;
    }

    private static string ReadFString(ReadOnlySpan<byte> data, ref int cursor)
    {
        EnsureRemaining(data, cursor, sizeof(int), "FString", "length");
        var serializedLength = BinaryPrimitives.ReadInt32LittleEndian(data.Slice(cursor, sizeof(int)));
        cursor += sizeof(int);
        if (serializedLength == 0)
        {
            return string.Empty;
        }

        if (serializedLength > 0)
        {
            var byteLength = serializedLength;
            EnsureRemaining(data, cursor, byteLength, "FString", "ANSI value");
            var bytes = data.Slice(cursor, byteLength);
            cursor += byteLength;
            if (bytes[^1] != 0)
            {
                throw new InvalidDataException("WorldOption.sav contains an unterminated ANSI FString.");
            }

            try
            {
                return StrictUtf8.GetString(bytes[..^1]);
            }
            catch (DecoderFallbackException exception)
            {
                throw new InvalidDataException("WorldOption.sav contains a non-UTF8 ANSI FString.", exception);
            }
        }

        if (serializedLength == int.MinValue)
        {
            throw new InvalidDataException("WorldOption.sav contains an invalid UTF-16 FString length.");
        }

        var characterCount = -serializedLength;
        var byteLengthUtf16 = checked(characterCount * sizeof(char));
        EnsureRemaining(data, cursor, byteLengthUtf16, "FString", "UTF-16 value");
        var utf16 = data.Slice(cursor, byteLengthUtf16);
        cursor += byteLengthUtf16;
        if (utf16.Length < 2 || utf16[^2] != 0 || utf16[^1] != 0)
        {
            throw new InvalidDataException("WorldOption.sav contains an unterminated UTF-16 FString.");
        }

        return Encoding.Unicode.GetString(utf16[..^2]);
    }

    private static void EnsureRemaining(
        ReadOnlySpan<byte> data,
        int cursor,
        int required,
        string path,
        string name)
    {
        if (required < 0 || cursor < 0 || cursor > data.Length - required)
        {
            throw new InvalidDataException($"WorldOption.sav is truncated while reading {path}.{name}.");
        }
    }

    private static byte[] EncodeFString(string value)
    {
        var text = Encoding.UTF8.GetBytes(value);
        var result = new byte[sizeof(int) + text.Length + 1];
        BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(0, sizeof(int)), text.Length + 1);
        text.CopyTo(result, sizeof(int));
        return result;
    }

    private static byte[] DecompressZlib(ReadOnlySpan<byte> compressed)
    {
        using var source = new MemoryStream(compressed.ToArray(), writable: false);
        using var zlib = new ZLibStream(source, CompressionMode.Decompress);
        using var destination = new MemoryStream();
        try
        {
            zlib.CopyTo(destination);
        }
        catch (InvalidDataException exception)
        {
            throw new InvalidDataException("WorldOption.sav contains invalid zlib data.", exception);
        }

        return destination.ToArray();
    }

    private sealed record DecodedWorldOption(string Container, byte[] Payload);

    private sealed record ParsedProperty(
        string Name,
        string PropertyType,
        string? ValueType,
        string? DisplayValue,
        ReadOnlyMemory<byte> ValueBytes);
}
