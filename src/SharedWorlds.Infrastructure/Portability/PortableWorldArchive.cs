using System.Buffers;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using SharedWorlds.Core.Portability;

namespace SharedWorlds.Infrastructure.Portability;

public sealed record PortableWorldArchiveLimits(
    long MaximumStateBytes = 64L * 1024 * 1024 * 1024,
    int MaximumManifestBytes = 1024 * 1024);

/// <summary>
/// Reads and writes the Safe World V1 portable artifact without interpreting game-specific state.
/// V1 deliberately contains exactly two root entries: manifest.json and state.bin.
/// </summary>
public static class PortableWorldArchive
{
    public const int CurrentFormatVersion = 1;
    public const string ManifestEntryName = "manifest.json";
    public const string StateEntryName = "state.bin";

    private const int CopyBufferSize = 128 * 1024;
    private const int MaximumAdapterIdLength = 128;
    private const int MaximumWorldNameLength = 512;
    private const int MaximumSnapshotIdLength = 128;
    private const int MaximumCreatorLength = 256;
    private const int MaximumDescriptionLength = 16 * 1024;
    private const int MaximumSourceUrlLength = 2048;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        MaxDepth = 32,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        WriteIndented = true
    };

    public static async Task<PortableWorldManifest> WriteAsync(
        Stream destination,
        PortableWorldDescription description,
        Stream statePackage,
        PortableWorldArchiveLimits? limits = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(description);
        ArgumentNullException.ThrowIfNull(statePackage);

        if (!destination.CanWrite)
        {
            throw new ArgumentException("Portable World destination must be writable.", nameof(destination));
        }

        if (!statePackage.CanRead)
        {
            throw new ArgumentException("World state package must be readable.", nameof(statePackage));
        }

        limits ??= new PortableWorldArchiveLimits();
        ValidateLimits(limits);
        ValidateDescription(description);

        using var archive = new ZipArchive(destination, ZipArchiveMode.Create, leaveOpen: true);

        var stateEntry = archive.CreateEntry(StateEntryName, CompressionLevel.NoCompression);
        long stateLength;
        byte[] stateHash;
        await using (var stateOutput = stateEntry.Open())
        using (var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
        {
            stateLength = await CopyBoundedAndHashAsync(
                statePackage,
                stateOutput,
                hash,
                limits.MaximumStateBytes,
                cancellationToken);
            stateHash = hash.GetHashAndReset();
        }

        var manifest = new PortableWorldManifest(
            FormatVersion: CurrentFormatVersion,
            GameAdapterId: description.GameAdapterId,
            WorldName: description.WorldName,
            SnapshotId: description.SnapshotId,
            CreatedAt: description.CreatedAt,
            Environment: description.Environment,
            Payload: new PortableWorldPayload(
                EntryName: StateEntryName,
                Length: stateLength,
                Sha256: Convert.ToHexString(stateHash).ToLowerInvariant()),
            Presentation: description.Presentation,
            StartedFrom: description.StartedFrom);

        ValidateManifest(manifest, limits);

        await using var manifestBuffer = new MemoryStream();
        await JsonSerializer.SerializeAsync(
            manifestBuffer,
            manifest,
            JsonOptions,
            cancellationToken);
        if (manifestBuffer.Length > limits.MaximumManifestBytes)
        {
            throw new InvalidDataException(
                $"Portable World manifest exceeds the {limits.MaximumManifestBytes}-byte limit.");
        }

        manifestBuffer.Position = 0;
        var manifestEntry = archive.CreateEntry(ManifestEntryName, CompressionLevel.Optimal);
        await using (var manifestOutput = manifestEntry.Open())
        {
            await manifestBuffer.CopyToAsync(manifestOutput, cancellationToken);
        }

        return manifest;
    }

    /// <summary>
    /// Validates a hostile portable artifact and copies its opaque state bytes into an empty,
    /// seekable staging stream. On any validation failure the staging stream is reset to empty.
    /// No archive path is ever extracted to the filesystem.
    /// </summary>
    public static async Task<PortableWorldManifest> ValidateAndExtractStateAsync(
        Stream source,
        Stream stateStaging,
        PortableWorldArchiveLimits? limits = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(stateStaging);

        if (!source.CanRead || !source.CanSeek)
        {
            throw new ArgumentException(
                "Portable World source must be readable and seekable.",
                nameof(source));
        }

        if (!stateStaging.CanWrite || !stateStaging.CanSeek)
        {
            throw new ArgumentException(
                "World state staging stream must be writable and seekable.",
                nameof(stateStaging));
        }

        if (stateStaging.Length != 0 || stateStaging.Position != 0)
        {
            throw new ArgumentException(
                "World state staging stream must be empty.",
                nameof(stateStaging));
        }

        limits ??= new PortableWorldArchiveLimits();
        ValidateLimits(limits);

        try
        {
            using var archive = new ZipArchive(source, ZipArchiveMode.Read, leaveOpen: true);
            if (archive.Entries.Count != 2)
            {
                throw new InvalidDataException(
                    "Safe World V1 artifacts must contain exactly manifest.json and state.bin.");
            }

            var manifestEntry = RequireRootEntry(archive, ManifestEntryName);
            var stateEntry = RequireRootEntry(archive, StateEntryName);

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
            await using (var manifestInput = manifestEntry.Open())
            {
                var manifestBytes = await ReadBoundedAsync(
                    manifestInput,
                    limits.MaximumManifestBytes,
                    cancellationToken);
                manifest = JsonSerializer.Deserialize<PortableWorldManifest>(manifestBytes, JsonOptions)
                           ?? throw new InvalidDataException("Portable World manifest is empty.");
            }

            ValidateManifest(manifest, limits);
            if (manifest.Payload.Length != stateEntry.Length)
            {
                throw new InvalidDataException(
                    "Portable World state length does not match the manifest.");
            }

            byte[] actualHash;
            long actualLength;
            await using (var stateInput = stateEntry.Open())
            using (var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            {
                actualLength = await CopyBoundedAndHashAsync(
                    stateInput,
                    stateStaging,
                    hash,
                    limits.MaximumStateBytes,
                    cancellationToken);
                actualHash = hash.GetHashAndReset();
            }

            if (actualLength != manifest.Payload.Length)
            {
                throw new InvalidDataException(
                    "Portable World state length changed while reading the artifact.");
            }

            var expectedHash = ParseSha256(manifest.Payload.Sha256);
            if (!CryptographicOperations.FixedTimeEquals(actualHash, expectedHash))
            {
                throw new InvalidDataException("Portable World state SHA-256 does not match the manifest.");
            }

            stateStaging.Position = 0;
            return manifest;
        }
        catch
        {
            stateStaging.SetLength(0);
            stateStaging.Position = 0;
            throw;
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

    private static void ValidateDescription(PortableWorldDescription description)
    {
        ValidateText(description.GameAdapterId, nameof(description.GameAdapterId), MaximumAdapterIdLength);
        ValidateText(description.WorldName, nameof(description.WorldName), MaximumWorldNameLength);
        ValidateText(description.SnapshotId, nameof(description.SnapshotId), MaximumSnapshotIdLength);
        ArgumentNullException.ThrowIfNull(description.Environment);

        if (!string.Equals(
                description.GameAdapterId,
                description.Environment.AdapterId,
                StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "Portable World game adapter must match the environment adapter.",
                nameof(description));
        }

        ValidatePresentation(description.Presentation);
        ValidateOrigin(description.StartedFrom);
    }

    private static void ValidateManifest(
        PortableWorldManifest manifest,
        PortableWorldArchiveLimits limits)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        if (manifest.FormatVersion != CurrentFormatVersion)
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
            !string.Equals(manifest.Payload.EntryName, StateEntryName, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Portable World V1 payload must be the exact root entry '{StateEntryName}'.");
        }

        if (manifest.Payload.Length < 0 || manifest.Payload.Length > limits.MaximumStateBytes)
        {
            throw new InvalidDataException("Portable World state length is outside the allowed bounds.");
        }

        _ = ParseSha256(manifest.Payload.Sha256);
        ValidatePresentation(manifest.Presentation);
        ValidateOrigin(manifest.StartedFrom);
    }

    private static void ValidatePresentation(PortableWorldPresentation? presentation)
    {
        if (presentation is null)
        {
            return;
        }

        ValidateOptionalText(presentation.Creator, nameof(presentation.Creator), MaximumCreatorLength);
        ValidateOptionalText(presentation.Description, nameof(presentation.Description), MaximumDescriptionLength);
        ValidateOptionalText(presentation.SourceUrl, nameof(presentation.SourceUrl), MaximumSourceUrlLength);

        if (!string.IsNullOrWhiteSpace(presentation.SourceUrl) &&
            (!Uri.TryCreate(presentation.SourceUrl, UriKind.Absolute, out var sourceUri) ||
             (sourceUri.Scheme != Uri.UriSchemeHttps && sourceUri.Scheme != Uri.UriSchemeHttp)))
        {
            throw new InvalidDataException("Portable World source URL must use HTTP or HTTPS.");
        }
    }

    private static void ValidateOrigin(PortableWorldOrigin? origin)
    {
        if (origin is null)
        {
            return;
        }

        ValidateText(origin.SnapshotId, nameof(origin.SnapshotId), MaximumSnapshotIdLength);
        ValidateOptionalText(origin.WorldName, nameof(origin.WorldName), MaximumWorldNameLength);
        ValidateOptionalText(origin.Creator, nameof(origin.Creator), MaximumCreatorLength);
    }

    private static void ValidateLimits(PortableWorldArchiveLimits limits)
    {
        if (limits.MaximumStateBytes < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(limits), "Maximum state bytes cannot be negative.");
        }

        if (limits.MaximumManifestBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(limits), "Maximum manifest bytes must be positive.");
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

    private static void ValidateOptionalText(string? value, string name, int maximumLength)
    {
        if (value is not null && value.Length > maximumLength)
        {
            throw new InvalidDataException(
                $"Portable World {name} exceeds the {maximumLength}-character limit.");
        }
    }

    private static byte[] ParseSha256(string value)
    {
        if (value.Length != 64)
        {
            throw new InvalidDataException("Portable World state SHA-256 must contain 64 hex characters.");
        }

        try
        {
            var bytes = Convert.FromHexString(value);
            if (bytes.Length != 32)
            {
                throw new InvalidDataException("Portable World state SHA-256 is invalid.");
            }

            return bytes;
        }
        catch (FormatException exception)
        {
            throw new InvalidDataException("Portable World state SHA-256 is invalid.", exception);
        }
    }

    private static async Task<byte[]> ReadBoundedAsync(
        Stream input,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        await using var output = new MemoryStream(Math.Min(maximumBytes, 64 * 1024));
        await CopyBoundedAsync(input, output, maximumBytes, cancellationToken);
        return output.ToArray();
    }

    private static async Task<long> CopyBoundedAndHashAsync(
        Stream input,
        Stream output,
        IncrementalHash hash,
        long maximumBytes,
        CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(CopyBufferSize);
        try
        {
            long total = 0;
            while (true)
            {
                var read = await input.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken);
                if (read == 0)
                {
                    return total;
                }

                if (total > maximumBytes - read)
                {
                    throw new InvalidDataException(
                        $"Portable World payload exceeds the {maximumBytes}-byte limit.");
                }

                hash.AppendData(buffer, 0, read);
                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                total += read;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static async Task<long> CopyBoundedAsync(
        Stream input,
        Stream output,
        long maximumBytes,
        CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(CopyBufferSize);
        try
        {
            long total = 0;
            while (true)
            {
                var read = await input.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken);
                if (read == 0)
                {
                    return total;
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
