using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;

namespace SharedWorlds.GameAdapters.Palworld;

/// <summary>
/// Container-aware entry point for the temporary WorldOption AdminPassword overlay.
/// Older PlZ saves continue through the existing zlib implementation. Current PlM
/// saves require Oodle only for decoding; the temporary runtime overlay is emitted as
/// legacy PlZ/0x31 because current Palworld remains able to read that container.
/// The GVAS property edit remains delegated to the already validated fail-closed patcher.
/// </summary>
internal static class PalworldWorldOptionAdminPasswordRuntimeOverlay
{
    private const int HeaderLength = 12;
    private const byte SingleCompressionSaveType = 0x31;

    private static readonly byte[] PlZMagic = "PlZ"u8.ToArray();
    private static readonly byte[] PlMMagic = "PlM"u8.ToArray();
    private static readonly byte[] GvasMagic = "GVAS"u8.ToArray();

    public static bool RequiresOodle(ReadOnlySpan<byte> save)
        => save.Length >= HeaderLength && save.Slice(8, 3).SequenceEqual(PlMMagic);

    public static string DescribeContainer(ReadOnlySpan<byte> save)
    {
        if (save.Length < HeaderLength)
        {
            return "truncated";
        }

        var magic = save.Slice(8, 3);
        return magic.SequenceEqual(PlMMagic)
            ? $"PlM/0x{save[11]:X2}"
            : magic.SequenceEqual(PlZMagic)
                ? $"PlZ/0x{save[11]:X2}"
                : $"unknown-{Convert.ToHexString(magic)}/0x{save[11]:X2}";
    }

    public static PalworldWorldOptionOverlayResult Create(
        ReadOnlySpan<byte> originalSave,
        string transientPassword,
        IPalworldOodleCodec? oodleCodec = null)
    {
        if (originalSave.Length < HeaderLength)
        {
            throw new InvalidDataException("WorldOption.sav is too small to contain a Palworld save wrapper.");
        }

        var magic = originalSave.Slice(8, 3);
        if (magic.SequenceEqual(PlZMagic))
        {
            return PalworldWorldOptionAdminPasswordOverlay.Create(originalSave, transientPassword);
        }

        if (!magic.SequenceEqual(PlMMagic))
        {
            throw new InvalidDataException(
                $"WorldOption.sav uses an unsupported Palworld container magic {Convert.ToHexString(magic)}.");
        }

        if (originalSave[11] != SingleCompressionSaveType)
        {
            throw new InvalidDataException(
                $"Current Palworld PlM WorldOption.sav uses unsupported save type 0x{originalSave[11]:X2}.");
        }

        if (oodleCodec is null)
        {
            throw new InvalidDataException(
                "Current Palworld PlM WorldOption.sav requires an installed Oodle codec for the acceptance overlay.");
        }

        var uncompressedLength = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(originalSave[..4]));
        var compressedLength = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(originalSave.Slice(4, 4)));
        var compressedPayload = originalSave[HeaderLength..];
        if (compressedLength != compressedPayload.Length)
        {
            throw new InvalidDataException(
                "Current Palworld PlM WorldOption.sav has an inconsistent compressed length.");
        }

        var originalPayload = oodleCodec.Decompress(compressedPayload, uncompressedLength);
        ValidateGvas(originalPayload);

        // Current Palworld can still read the legacy PlZ/0x31 container. Use that as
        // the temporary runtime representation so an external Oodle runtime is needed
        // only to decode the canonical PlM input, never to generate bytes PalServer must trust.
        var syntheticLegacy = WrapSingleZlib(originalPayload);
        var legacyPatched = PalworldWorldOptionAdminPasswordOverlay.Create(
            syntheticLegacy,
            transientPassword);
        var patchedPayload = UnwrapSingleZlib(legacyPatched.PatchedSave);
        ValidateGvas(patchedPayload);

        return new PalworldWorldOptionOverlayResult(
            legacyPatched.PatchedSave,
            Sha256(originalSave),
            Sha256(legacyPatched.PatchedSave),
            SingleCompressionSaveType,
            legacyPatched.ExistingAdminPasswordConfigured,
            originalPayload.Length,
            patchedPayload.Length);
    }

    private static void ValidateGvas(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < GvasMagic.Length || !payload[..GvasMagic.Length].SequenceEqual(GvasMagic))
        {
            throw new InvalidDataException(
                "Decompressed WorldOption.sav does not begin with the expected GVAS header.");
        }
    }

    private static byte[] WrapSingleZlib(ReadOnlySpan<byte> payload)
    {
        var compressed = CompressZlib(payload);
        var result = new byte[HeaderLength + compressed.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(0, 4), checked((uint)payload.Length));
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(4, 4), checked((uint)compressed.Length));
        PlZMagic.CopyTo(result, 8);
        result[11] = SingleCompressionSaveType;
        compressed.CopyTo(result, HeaderLength);
        return result;
    }

    private static byte[] UnwrapSingleZlib(ReadOnlySpan<byte> save)
    {
        if (save.Length < HeaderLength ||
            !save.Slice(8, 3).SequenceEqual(PlZMagic) ||
            save[11] != SingleCompressionSaveType)
        {
            throw new InvalidDataException("The internal WorldOption PlZ bridge produced an invalid container.");
        }

        var uncompressedLength = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(save[..4]));
        var compressedLength = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(save.Slice(4, 4)));
        var compressed = save[HeaderLength..];
        if (compressedLength != compressed.Length)
        {
            throw new InvalidDataException("The internal WorldOption PlZ bridge has an inconsistent compressed length.");
        }

        using var source = new MemoryStream(compressed.ToArray(), writable: false);
        using var zlib = new ZLibStream(source, CompressionMode.Decompress);
        using var destination = new MemoryStream();
        zlib.CopyTo(destination);
        var payload = destination.ToArray();
        if (payload.Length != uncompressedLength)
        {
            throw new InvalidDataException("The internal WorldOption PlZ bridge has an inconsistent uncompressed length.");
        }

        return payload;
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
}
