using System.Buffers;
using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;
using SharedWorlds.Core.Environment;
using SharedWorlds.Core.Portability;

namespace SharedWorlds.Infrastructure.Portability;

/// <summary>
/// Bounded metadata available before Safe World copies and hashes state.bin.
/// This is suitable for adapter routing, disk-space preflight, and preview copy only.
/// It is not proof that the portable World is valid; import must still run the authoritative
/// PortableWorldArchive.ValidateAndExtractStateAsync path before any World is published.
/// </summary>
public sealed record PortableWorldPreflight(
    string GameAdapterId,
    string WorldName,
    string SnapshotId,
    DateTimeOffset CreatedAt,
    EnvironmentManifest Environment,
    long StateBytes,
    PortableWorldPresentation? Presentation);

/// <summary>
/// Reads only bounded archive metadata needed before full hostile-input validation/materialization.
/// The opaque state payload is never opened by this inspector.
/// </summary>
public static class PortableWorldArchiveInspector
{
    private const int CopyBufferSize = 128 * 1024;
    private const int MaximumAdapterIdLength = 128;
    private const int MaximumWorldNameLength = 512;
    private const int MaximumSnapshotIdLength = 128;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        MaxDepth = 32,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    public static async Task<PortableWorldPreflight> InspectAsync(
        Stream source,
        PortableWorldArchiveLimits? limits = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        cancellationToken.ThrowIfCancellationRequested();

        if (!source.CanRead || !source.CanSeek)
        {
            throw new ArgumentException(
                "Portable World source must be readable and seekable.",
                nameof(source));
        }

        limits ??= new PortableWorldArchiveLimits();
        if (limits.MaximumStateBytes < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(limits), "Maximum state bytes cannot be negative.");
        }

        if (limits.MaximumManifestBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(limits), "Maximum manifest bytes must be positive.");
        }

        source.Position = 0;
        try
        {
            using var archive = new ZipArchive(source, ZipArchiveMode.Read, leaveOpen: true);
            if (archive.Entries.Count != 2)
            {
                throw new InvalidDataException(
                    "Safe World V1 artifacts must contain exactly manifest.json and state.bin.");
            }

            var manifestEntry = RequireRootEntry(archive, PortableWorldArchive.ManifestEntryName);
            var stateEntry = RequireRootEntry(archive, PortableWorldArchive.StateEntryName);

            if (manifestEntry.Length > limits.MaximumManifestBytes)
            {
                throw new InvalidDataException(
                    $"Portable World manifest exceeds the {limits.MaximumManifestBytes}-byte limit.");
            }

            if (stateEntry.Length > limits.MaximumStateBytes)
            {
                throw new InvalidDataException(
                    $"Portable World state exceeds the {limits.MaximumStateBytes}-byte limit.");
            }

            PortableWorldManifest manifest;
            await using (var input = manifestEntry.Open())
            {
                var bytes = await ReadBoundedAsync(
                    input,
                    limits.MaximumManifestBytes,
                    cancellationToken);
                manifest = JsonSerializer.Deserialize<PortableWorldManifest>(bytes, JsonOptions)
                           ?? throw new InvalidDataException("Portable World manifest is empty.");
            }

            ValidateRoutingMetadata(manifest, stateEntry.Length, limits);
            return new PortableWorldPreflight(
                manifest.GameAdapterId,
                manifest.WorldName,
                manifest.SnapshotId,
                manifest.CreatedAt,
                manifest.Environment,
                manifest.Payload.Length,
                manifest.Presentation);
        }
        finally
        {
            source.Position = 0;
        }
    }

    private static ZipArchiveEntry RequireRootEntry(ZipArchive archive, string name)
    {
        var matches = archive.Entries
            .Where(entry => string.Equals(entry.FullName, name, StringComparison.Ordinal))
            .ToArray();
        if (matches.Length != 1 || !string.Equals(matches[0].Name, name, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Portable World artifact is missing the exact root entry '{name}'.");
        }

        return matches[0];
    }

    private static void ValidateRoutingMetadata(
        PortableWorldManifest manifest,
        long stateEntryLength,
        PortableWorldArchiveLimits limits)
    {
        if (manifest.FormatVersion != PortableWorldArchive.CurrentFormatVersion)
        {
            throw new InvalidDataException(
                $"Unsupported Safe World format version {manifest.FormatVersion}.");
        }

        ValidateText(manifest.GameAdapterId, nameof(manifest.GameAdapterId), MaximumAdapterIdLength);
        ValidateText(manifest.WorldName, nameof(manifest.WorldName), MaximumWorldNameLength);
        ValidateText(manifest.SnapshotId, nameof(manifest.SnapshotId), MaximumSnapshotIdLength);

        if (manifest.Environment is null)
        {
            throw new InvalidDataException("Portable World manifest has no environment.");
        }

        if (!string.Equals(
                manifest.GameAdapterId,
                manifest.Environment.AdapterId,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "Portable World game adapter does not match the environment adapter.");
        }

        if (manifest.Payload is null ||
            !string.Equals(
                manifest.Payload.EntryName,
                PortableWorldArchive.StateEntryName,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Portable World V1 payload must be the exact root entry '{PortableWorldArchive.StateEntryName}'.");
        }

        if (manifest.Payload.Length < 0 || manifest.Payload.Length > limits.MaximumStateBytes)
        {
            throw new InvalidDataException("Portable World state length is outside the allowed bounds.");
        }

        if (manifest.Payload.Length != stateEntryLength)
        {
            throw new InvalidDataException(
                "Portable World state length does not match the manifest.");
        }
    }

    private static void ValidateText(string value, string name, int maximumLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidDataException($"Portable World {name} is required.");
        }

        if (value.Length > maximumLength)
        {
            throw new InvalidDataException(
                $"Portable World {name} exceeds the {maximumLength}-character limit.");
        }
    }

    private static async Task<byte[]> ReadBoundedAsync(
        Stream input,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        await using var output = new MemoryStream(Math.Min(maximumBytes, 64 * 1024));
        var buffer = ArrayPool<byte>.Shared.Rent(CopyBufferSize);
        try
        {
            long total = 0;
            while (true)
            {
                var read = await input.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken);
                if (read == 0)
                {
                    return output.ToArray();
                }

                if (total > maximumBytes - read)
                {
                    throw new InvalidDataException(
                        $"Portable World payload exceeds the {maximumBytes}-byte limit.");
                }

                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                total += read;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}
