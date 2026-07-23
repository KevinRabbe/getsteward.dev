using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace SharedWorlds.GameAdapters.Palworld;

internal sealed record PalworldWorldOptionOverlayResult(
    byte[] PatchedSave,
    string OriginalSaveSha256,
    string PatchedSaveSha256,
    int SaveType,
    bool ExistingAdminPasswordConfigured,
    int OriginalPayloadLength,
    int PatchedPayloadLength);

/// <summary>
/// Creates a temporary Palworld WorldOption.sav variant whose only semantic change is
/// the AdminPassword StrProperty value. The original save is never rewritten by this helper.
///
/// This intentionally does not deserialize and reserialize the complete GVAS document. It
/// validates the unique AdminPassword property tag, replaces only that property's encoded
/// FString and size, then verifies that applying the original value restores the decompressed
/// payload byte-for-byte. Unknown wrappers or ambiguous property layouts fail closed.
/// </summary>
internal static class PalworldWorldOptionAdminPasswordOverlay
{
    private const int HeaderLength = 12;
    private const byte SingleZlibSaveType = 0x31;
    private const byte DoubleZlibSaveType = 0x32;
    private const int MaximumPasswordCharacters = 256;

    private static readonly byte[] Magic = "PlZ"u8.ToArray();
    private static readonly byte[] AdminPasswordName = EncodeFString("AdminPassword");
    private static readonly byte[] StrPropertyType = EncodeFString("StrProperty");
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    public static PalworldWorldOptionOverlayResult Create(
        ReadOnlySpan<byte> originalSave,
        string transientPassword)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(transientPassword);
        if (transientPassword.Length > MaximumPasswordCharacters)
        {
            throw new ArgumentOutOfRangeException(
                nameof(transientPassword),
                $"Palworld acceptance passwords are limited to {MaximumPasswordCharacters} characters.");
        }

        if (transientPassword.IndexOf('\0', StringComparison.Ordinal) >= 0)
        {
            throw new ArgumentException("Palworld acceptance passwords cannot contain NUL characters.", nameof(transientPassword));
        }

        var decoded = DecodeSave(originalSave);
        var originalProperty = FindSingleAdminPasswordProperty(decoded.Payload);
        var patchedPayload = ReplacePropertyValue(decoded.Payload, originalProperty, transientPassword);

        var patchedProperty = FindSingleAdminPasswordProperty(patchedPayload);
        if (!string.Equals(patchedProperty.Value, transientPassword, StringComparison.Ordinal))
        {
            throw new InvalidDataException("The temporary WorldOption AdminPassword did not round-trip after patching.");
        }

        var revertedPayload = ReplacePropertyValue(patchedPayload, patchedProperty, originalProperty.Value);
        if (!revertedPayload.AsSpan().SequenceEqual(decoded.Payload))
        {
            throw new InvalidDataException(
                "The WorldOption AdminPassword overlay changed bytes outside the validated property boundary.");
        }

        var patchedSave = EncodeSave(patchedPayload, decoded.SaveType);
        return new PalworldWorldOptionOverlayResult(
            patchedSave,
            Sha256(originalSave),
            Sha256(patchedSave),
            decoded.SaveType,
            !string.IsNullOrEmpty(originalProperty.Value),
            decoded.Payload.Length,
            patchedPayload.Length);
    }

    private static DecodedSave DecodeSave(ReadOnlySpan<byte> save)
    {
        if (save.Length < HeaderLength)
        {
            throw new InvalidDataException("WorldOption.sav is too small to contain a Palworld save wrapper.");
        }

        if (!save.Slice(8, Magic.Length).SequenceEqual(Magic))
        {
            throw new InvalidDataException("WorldOption.sav does not use the expected Palworld PlZ wrapper.");
        }

        var uncompressedLength = BinaryPrimitives.ReadUInt32LittleEndian(save[..4]);
        var compressedLength = BinaryPrimitives.ReadUInt32LittleEndian(save.Slice(4, 4));
        var saveType = save[11];
        var compressedPayload = save[HeaderLength..];

        byte[] payload;
        switch (saveType)
        {
            case SingleZlibSaveType:
                if (compressedLength != compressedPayload.Length)
                {
                    throw new InvalidDataException("WorldOption.sav has an inconsistent compressed length.");
                }

                payload = DecompressZlib(compressedPayload);
                break;

            case DoubleZlibSaveType:
                var innerCompressed = DecompressZlib(compressedPayload);
                if (compressedLength != innerCompressed.Length)
                {
                    throw new InvalidDataException("WorldOption.sav has an inconsistent inner compressed length.");
                }

                payload = DecompressZlib(innerCompressed);
                break;

            default:
                throw new InvalidDataException($"WorldOption.sav uses unsupported Palworld save type 0x{saveType:X2}.");
        }

        if (uncompressedLength != payload.Length)
        {
            throw new InvalidDataException("WorldOption.sav has an inconsistent uncompressed length.");
        }

        return new DecodedSave(payload, saveType);
    }

    private static byte[] EncodeSave(ReadOnlySpan<byte> payload, byte saveType)
    {
        byte[] innerCompressed;
        byte[] finalPayload;
        switch (saveType)
        {
            case SingleZlibSaveType:
                innerCompressed = CompressZlib(payload);
                finalPayload = innerCompressed;
                break;

            case DoubleZlibSaveType:
                innerCompressed = CompressZlib(payload);
                finalPayload = CompressZlib(innerCompressed);
                break;

            default:
                throw new InvalidDataException($"WorldOption.sav uses unsupported Palworld save type 0x{saveType:X2}.");
        }

        var result = new byte[HeaderLength + finalPayload.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(0, 4), checked((uint)payload.Length));
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(4, 4), checked((uint)innerCompressed.Length));
        Magic.CopyTo(result, 8);
        result[11] = saveType;
        finalPayload.CopyTo(result, HeaderLength);
        return result;
    }

    private static AdminPasswordProperty FindSingleAdminPasswordProperty(ReadOnlySpan<byte> payload)
    {
        AdminPasswordProperty? match = null;
        var searchOffset = 0;

        while (TryFindSequence(payload, AdminPasswordName, searchOffset, out var propertyNameOffset))
        {
            searchOffset = propertyNameOffset + AdminPasswordName.Length;
            var typeOffset = propertyNameOffset + AdminPasswordName.Length;
            if (typeOffset > payload.Length - StrPropertyType.Length ||
                !payload.Slice(typeOffset, StrPropertyType.Length).SequenceEqual(StrPropertyType))
            {
                continue;
            }

            var sizeOffset = typeOffset + StrPropertyType.Length;
            if (sizeOffset > payload.Length - sizeof(ulong))
            {
                continue;
            }

            var declaredValueSize = BinaryPrimitives.ReadUInt64LittleEndian(payload.Slice(sizeOffset, sizeof(ulong)));
            var cursor = sizeOffset + sizeof(ulong);
            if (cursor >= payload.Length)
            {
                continue;
            }

            var hasPropertyGuid = payload[cursor++];
            if (hasPropertyGuid == 1)
            {
                const int guidLength = 16;
                if (cursor > payload.Length - guidLength)
                {
                    continue;
                }

                cursor += guidLength;
            }
            else if (hasPropertyGuid != 0)
            {
                continue;
            }

            FString value;
            try
            {
                value = ReadFString(payload, cursor);
            }
            catch (InvalidDataException)
            {
                continue;
            }

            if (declaredValueSize != (ulong)value.TotalBytes)
            {
                continue;
            }

            var candidate = new AdminPasswordProperty(sizeOffset, cursor, value.TotalBytes, value.Value);
            if (match is not null)
            {
                throw new InvalidDataException(
                    "WorldOption.sav contains more than one structurally valid AdminPassword StrProperty.");
            }

            match = candidate;
        }

        return match ?? throw new InvalidDataException(
            "WorldOption.sav does not contain one structurally valid AdminPassword StrProperty.");
    }

    private static byte[] ReplacePropertyValue(
        ReadOnlySpan<byte> payload,
        AdminPasswordProperty property,
        string replacementValue)
    {
        var replacement = EncodeFString(replacementValue);
        var newLength = checked(payload.Length - property.ValueLength + replacement.Length);
        var result = new byte[newLength];

        payload[..property.SizeOffset].CopyTo(result);
        BinaryPrimitives.WriteUInt64LittleEndian(
            result.AsSpan(property.SizeOffset, sizeof(ulong)),
            checked((ulong)replacement.Length));

        var metadataStart = property.SizeOffset + sizeof(ulong);
        payload.Slice(metadataStart, property.ValueOffset - metadataStart)
            .CopyTo(result.AsSpan(metadataStart));
        replacement.CopyTo(result, property.ValueOffset);

        var originalTailOffset = property.ValueOffset + property.ValueLength;
        var replacementTailOffset = property.ValueOffset + replacement.Length;
        payload[originalTailOffset..].CopyTo(result.AsSpan(replacementTailOffset));
        return result;
    }

    private static FString ReadFString(ReadOnlySpan<byte> payload, int offset)
    {
        if (offset < 0 || offset > payload.Length - sizeof(int))
        {
            throw new InvalidDataException("WorldOption.sav contains a truncated FString length.");
        }

        var serializedLength = BinaryPrimitives.ReadInt32LittleEndian(payload.Slice(offset, sizeof(int)));
        if (serializedLength == 0)
        {
            return new FString(string.Empty, sizeof(int));
        }

        if (serializedLength > 0)
        {
            var byteLength = serializedLength;
            if (byteLength < 1 || offset + sizeof(int) > payload.Length - byteLength)
            {
                throw new InvalidDataException("WorldOption.sav contains a truncated ANSI FString.");
            }

            var bytes = payload.Slice(offset + sizeof(int), byteLength);
            if (bytes[^1] != 0)
            {
                throw new InvalidDataException("WorldOption.sav contains an unterminated ANSI FString.");
            }

            string value;
            try
            {
                value = StrictUtf8.GetString(bytes[..^1]);
            }
            catch (DecoderFallbackException exception)
            {
                throw new InvalidDataException("WorldOption.sav contains a non-UTF8 ANSI FString.", exception);
            }

            return new FString(value, checked(sizeof(int) + byteLength));
        }

        if (serializedLength == int.MinValue)
        {
            throw new InvalidDataException("WorldOption.sav contains an invalid UTF-16 FString length.");
        }

        var characterCount = -serializedLength;
        var utf16ByteLength = checked(characterCount * sizeof(char));
        if (characterCount < 1 || offset + sizeof(int) > payload.Length - utf16ByteLength)
        {
            throw new InvalidDataException("WorldOption.sav contains a truncated UTF-16 FString.");
        }

        var utf16 = payload.Slice(offset + sizeof(int), utf16ByteLength);
        if (utf16[^2] != 0 || utf16[^1] != 0)
        {
            throw new InvalidDataException("WorldOption.sav contains an unterminated UTF-16 FString.");
        }

        return new FString(
            Encoding.Unicode.GetString(utf16[..^2]),
            checked(sizeof(int) + utf16ByteLength));
    }

    private static byte[] EncodeFString(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Length == 0)
        {
            return new byte[sizeof(int)];
        }

        if (value.All(character => character <= 0x7F))
        {
            var text = Encoding.UTF8.GetBytes(value);
            var result = new byte[sizeof(int) + text.Length + 1];
            BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(0, sizeof(int)), checked(text.Length + 1));
            text.CopyTo(result, sizeof(int));
            return result;
        }

        var utf16 = Encoding.Unicode.GetBytes(value);
        var resultUtf16 = new byte[sizeof(int) + utf16.Length + sizeof(char)];
        BinaryPrimitives.WriteInt32LittleEndian(
            resultUtf16.AsSpan(0, sizeof(int)),
            checked(-(value.Length + 1)));
        utf16.CopyTo(resultUtf16, sizeof(int));
        return resultUtf16;
    }

    private static bool TryFindSequence(
        ReadOnlySpan<byte> haystack,
        ReadOnlySpan<byte> needle,
        int startOffset,
        out int offset)
    {
        if (startOffset < 0 || startOffset > haystack.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(startOffset));
        }

        var relative = haystack[startOffset..].IndexOf(needle);
        if (relative < 0)
        {
            offset = -1;
            return false;
        }

        offset = startOffset + relative;
        return true;
    }

    private static byte[] DecompressZlib(ReadOnlySpan<byte> compressed)
    {
        using var source = new MemoryStream(compressed.ToArray(), writable: false);
        using var zlib = new ZLibStream(source, CompressionMode.Decompress, leaveOpen: false);
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

    private static byte[] CompressZlib(ReadOnlySpan<byte> payload)
    {
        using var destination = new MemoryStream();
        using (var zlib = new ZLibStream(destination, CompressionLevel.Optimal, leaveOpen: true))
        {
            zlib.Write(payload);
        }

        return destination.ToArray();
    }

    private static string Sha256(ReadOnlySpan<byte> value)
        => Convert.ToHexString(SHA256.HashData(value));

    private sealed record DecodedSave(byte[] Payload, byte SaveType);

    private sealed record AdminPasswordProperty(
        int SizeOffset,
        int ValueOffset,
        int ValueLength,
        string Value);

    private sealed record FString(string Value, int TotalBytes);
}
