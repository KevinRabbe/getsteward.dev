using System.Security.Cryptography;
using System.Text.Json;
using SharedWorlds.Core.Abstractions;
using SharedWorlds.Core.Domain;

namespace SharedWorlds.Infrastructure.Storage;

public sealed class LocalWorldStorage : IWorldStorage
{
    private const string PayloadFileName = "payload.bin";
    private const string PayloadSha256FileName = "payload.sha256";
    private readonly string _rootPath;

    public LocalWorldStorage(string rootPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        _rootPath = Path.GetFullPath(rootPath);
        Directory.CreateDirectory(_rootPath);
    }

    public Task SaveWorldAsync(World world, CancellationToken cancellationToken = default)
        => WriteDocumentAtomicAsync(
            GetWorldMetadataPath(world.Id),
            StorageDocumentSchemas.World,
            world,
            overwrite: true,
            cancellationToken);

    public async Task<World?> LoadWorldAsync(
        WorldId worldId,
        CancellationToken cancellationToken = default)
    {
        var path = GetWorldMetadataPath(worldId);
        if (!File.Exists(path))
        {
            return null;
        }

        await using var stream = OpenRead(path);
        return await PersistedDocumentCodec.ReadAsync(
            stream,
            StorageDocumentSchemas.World,
            cancellationToken);
    }

    public async Task<IReadOnlyList<World>> ListWorldsAsync(
        CancellationToken cancellationToken = default)
    {
        var worldsRoot = Path.Combine(_rootPath, "worlds");
        if (!Directory.Exists(worldsRoot))
        {
            return [];
        }

        var worlds = new List<World>();
        foreach (var directory in Directory.EnumerateDirectories(worldsRoot))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var metadataPath = Path.Combine(directory, "world.json");
            if (!File.Exists(metadataPath))
            {
                continue;
            }

            await using var stream = OpenRead(metadataPath);
            var world = await PersistedDocumentCodec.ReadAsync(
                stream,
                StorageDocumentSchemas.World,
                cancellationToken);
            worlds.Add(world);
        }

        return worlds
            .OrderBy(world => world.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(world => world.Id.ToString(), StringComparer.Ordinal)
            .ToArray();
    }

    public Task StoreEnvironmentRevisionAsync(
        EnvironmentRevision revision,
        CancellationToken cancellationToken = default)
        => WriteDocumentAtomicAsync(
            GetEnvironmentRevisionPath(revision.WorldId, revision.Id),
            StorageDocumentSchemas.EnvironmentRevision,
            revision,
            overwrite: false,
            cancellationToken);

    public async Task<EnvironmentRevision?> LoadEnvironmentRevisionAsync(
        WorldId worldId,
        RevisionId revisionId,
        CancellationToken cancellationToken = default)
    {
        var path = GetEnvironmentRevisionPath(worldId, revisionId);
        if (!File.Exists(path))
        {
            return null;
        }

        await using var stream = OpenRead(path);
        return await PersistedDocumentCodec.ReadAsync(
            stream,
            StorageDocumentSchemas.EnvironmentRevision,
            cancellationToken);
    }

    public async Task StoreRevisionAsync(
        StateRevision revision,
        Stream package,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(package);

        var finalDirectory = GetStateRevisionDirectory(revision.WorldId, revision.Id);
        var parentDirectory = Path.GetDirectoryName(finalDirectory)
            ?? throw new InvalidOperationException(
                $"Cannot resolve parent directory for state revision '{revision.Id}'.");
        Directory.CreateDirectory(parentDirectory);

        if (Directory.Exists(finalDirectory))
        {
            throw new IOException(
                $"State revision '{revision.Id}' for World '{revision.WorldId}' already exists and is immutable.");
        }

        var temporaryDirectory = finalDirectory + $".{Guid.NewGuid():N}.tmp";
        Directory.CreateDirectory(temporaryDirectory);

        try
        {
            await WriteDocumentFileAsync(
                Path.Combine(temporaryDirectory, "revision.json"),
                StorageDocumentSchemas.StateRevision,
                revision,
                cancellationToken);

            var payloadPath = Path.Combine(temporaryDirectory, PayloadFileName);
            var payloadSha256 = await CopyPayloadWithSha256Async(
                package,
                payloadPath,
                cancellationToken);
            await File.WriteAllTextAsync(
                Path.Combine(temporaryDirectory, PayloadSha256FileName),
                payloadSha256,
                cancellationToken);

            // Publish metadata, payload, and its integrity digest together only after all three are
            // fully written. A revision directory therefore never advertises a checksum for bytes
            // that were not durably staged with it.
            Directory.Move(temporaryDirectory, finalDirectory);
        }
        finally
        {
            TryDeleteDirectory(temporaryDirectory);
        }
    }

    public async Task<StateRevision?> LoadStateRevisionAsync(
        WorldId worldId,
        RevisionId revisionId,
        CancellationToken cancellationToken = default)
    {
        var path = Path.Combine(GetStateRevisionDirectory(worldId, revisionId), "revision.json");
        if (!File.Exists(path))
        {
            return null;
        }

        await using var stream = OpenRead(path);
        return await PersistedDocumentCodec.ReadAsync(
            stream,
            StorageDocumentSchemas.StateRevision,
            cancellationToken);
    }

    public async Task<Stream> OpenRevisionAsync(
        WorldId worldId,
        RevisionId revisionId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var revisionDirectory = GetStateRevisionDirectory(worldId, revisionId);
        var path = Path.Combine(revisionDirectory, PayloadFileName);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException(
                $"State revision '{revisionId}' for World '{worldId}' does not exist.",
                path);
        }

        var checksumPath = Path.Combine(revisionDirectory, PayloadSha256FileName);
        if (!File.Exists(checksumPath))
        {
            var persistedSchemaVersion = await ReadStateRevisionSchemaVersionAsync(
                revisionDirectory,
                cancellationToken);
            if (persistedSchemaVersion is >= 2)
            {
                throw new InvalidDataException(
                    $"State revision '{revisionId}' for World '{worldId}' is missing its required SHA-256 integrity digest.");
            }

            // Pre-checksum schema 0/1 revisions remain readable for persistence compatibility.
            return OpenRead(path);
        }

        var checksumText = (await File.ReadAllTextAsync(checksumPath, cancellationToken)).Trim();
        byte[] expectedHash;
        try
        {
            expectedHash = Convert.FromHexString(checksumText);
        }
        catch (FormatException exception)
        {
            throw new InvalidDataException(
                $"State revision '{revisionId}' for World '{worldId}' has an invalid SHA-256 integrity digest.",
                exception);
        }

        if (expectedHash.Length != SHA256.HashSizeInBytes)
        {
            throw new InvalidDataException(
                $"State revision '{revisionId}' for World '{worldId}' has an invalid SHA-256 integrity digest length.");
        }

        return new Sha256VerifyingReadStream(
            OpenRead(path),
            expectedHash,
            $"State revision '{revisionId}' for World '{worldId}' payload");
    }

    private static async Task<int?> ReadStateRevisionSchemaVersionAsync(
        string revisionDirectory,
        CancellationToken cancellationToken)
    {
        var metadataPath = Path.Combine(revisionDirectory, "revision.json");
        if (!File.Exists(metadataPath))
        {
            return null;
        }

        await using var stream = OpenRead(metadataPath);
        using var document = await JsonDocument.ParseAsync(
            stream,
            cancellationToken: cancellationToken);
        var root = document.RootElement;
        var looksLikeEnvelope = root.ValueKind == JsonValueKind.Object &&
                                (root.TryGetProperty("documentType", out _) ||
                                 root.TryGetProperty("schemaVersion", out _) ||
                                 root.TryGetProperty("payload", out _));
        if (!looksLikeEnvelope)
        {
            return 0;
        }

        if (!root.TryGetProperty("documentType", out var documentTypeElement) ||
            documentTypeElement.ValueKind != JsonValueKind.String ||
            !string.Equals(
                documentTypeElement.GetString(),
                StorageDocumentSchemas.StateRevision.DocumentType,
                StringComparison.Ordinal) ||
            !root.TryGetProperty("schemaVersion", out var schemaVersionElement) ||
            schemaVersionElement.ValueKind != JsonValueKind.Number ||
            !schemaVersionElement.TryGetInt32(out var schemaVersion) ||
            !root.TryGetProperty("payload", out _))
        {
            throw new InvalidDataException(
                "Persisted state revision envelope is incomplete, malformed, or has the wrong document type.");
        }

        return schemaVersion;
    }

    private static async Task<string> CopyPayloadWithSha256Async(
        Stream source,
        string destination,
        CancellationToken cancellationToken)
    {
        await using var output = new FileStream(
            destination,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 1024 * 128,
            useAsync: true);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[1024 * 128];

        while (true)
        {
            var read = await source.ReadAsync(buffer, cancellationToken);
            if (read == 0)
            {
                break;
            }

            hash.AppendData(buffer, 0, read);
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }

        await output.FlushAsync(cancellationToken);
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private async Task WriteDocumentAtomicAsync<T>(
        string destination,
        PersistedDocumentSchema<T> schema,
        T value,
        bool overwrite,
        CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(destination)
            ?? throw new InvalidOperationException($"Cannot resolve directory for '{destination}'.");

        Directory.CreateDirectory(directory);
        var temporary = destination + $".{Guid.NewGuid():N}.tmp";

        try
        {
            await WriteDocumentFileAsync(temporary, schema, value, cancellationToken);
            File.Move(temporary, destination, overwrite);
        }
        finally
        {
            TryDelete(temporary);
        }
    }

    private static async Task WriteDocumentFileAsync<T>(
        string destination,
        PersistedDocumentSchema<T> schema,
        T value,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            destination,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 64 * 1024,
            useAsync: true);

        await PersistedDocumentCodec.WriteAsync(
            stream,
            schema,
            value,
            cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    private string GetWorldDirectory(WorldId worldId)
        => Path.Combine(_rootPath, "worlds", worldId.ToString());

    private string GetWorldMetadataPath(WorldId worldId)
        => Path.Combine(GetWorldDirectory(worldId), "world.json");

    private string GetEnvironmentRevisionPath(WorldId worldId, RevisionId revisionId)
        => Path.Combine(
            GetWorldDirectory(worldId),
            "environments",
            $"{revisionId}.json");

    private string GetStateRevisionDirectory(WorldId worldId, RevisionId revisionId)
        => Path.Combine(
            GetWorldDirectory(worldId),
            "states",
            revisionId.ToString());

    private static FileStream OpenRead(string path)
        => new(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 1024 * 128,
            useAsync: true);

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // Best-effort cleanup only.
        }
        catch (UnauthorizedAccessException)
        {
            // Best-effort cleanup only.
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (IOException)
        {
            // Best-effort cleanup only.
        }
        catch (UnauthorizedAccessException)
        {
            // Best-effort cleanup only.
        }
    }
}
