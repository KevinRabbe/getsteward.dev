using System.Security.Cryptography;
using System.Text.Json;
using SharedWorlds.Core.Errors;

namespace SharedWorlds.Infrastructure.Storage;

internal sealed record PersistedDocumentEnvelope(
    string DocumentType,
    int SchemaVersion,
    int IntegrityVersion,
    string ContentSha256,
    JsonElement Payload);

internal sealed record PersistedDocumentSchema<T>(
    string DocumentType,
    int CurrentVersion,
    int IntegrityRequiredFromVersion,
    IReadOnlyDictionary<int, Func<JsonElement, T>> Migrations);

internal static class PersistedDocumentCodec
{
    private const int CurrentIntegrityVersion = 1;
    private const long MaximumDocumentBytes = 16L * 1024 * 1024;

    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    public static Task WriteAsync<T>(
        Stream destination,
        PersistedDocumentSchema<T> schema,
        T payload,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(schema);

        var payloadElement = JsonSerializer.SerializeToElement(payload, JsonOptions);
        var envelope = new PersistedDocumentEnvelope(
            schema.DocumentType,
            schema.CurrentVersion,
            CurrentIntegrityVersion,
            ComputeContentSha256(schema.DocumentType, schema.CurrentVersion, payloadElement),
            payloadElement);

        return JsonSerializer.SerializeAsync(
            destination,
            envelope,
            JsonOptions,
            cancellationToken);
    }

    public static async Task<T> ReadAsync<T>(
        Stream source,
        PersistedDocumentSchema<T> schema,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(schema);

        using var document = await ParseBoundedAsync(
            source,
            schema.DocumentType,
            cancellationToken);

        var root = document.RootElement;
        if (!LooksLikeEnvelope(root))
        {
            return Migrate(schema, sourceVersion: 0, root);
        }

        if (!root.TryGetProperty("documentType", out var documentTypeElement) ||
            documentTypeElement.ValueKind != JsonValueKind.String ||
            !root.TryGetProperty("schemaVersion", out var schemaVersionElement) ||
            schemaVersionElement.ValueKind != JsonValueKind.Number ||
            !root.TryGetProperty("payload", out var payloadElement))
        {
            throw new InvalidDataException("Persisted document envelope is incomplete or malformed.");
        }

        var documentType = documentTypeElement.GetString();
        if (!string.Equals(documentType, schema.DocumentType, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Persisted document type '{documentType}' does not match expected type '{schema.DocumentType}'.");
        }

        if (!schemaVersionElement.TryGetInt32(out var sourceVersion))
        {
            throw new InvalidDataException("Persisted document schema version is not a valid 32-bit integer.");
        }

        // A newer payload schema may also introduce a newer integrity mechanism. Preserve the
        // compatibility error instead of misclassifying an unknown future document as corrupt.
        if (sourceVersion > schema.CurrentVersion)
        {
            return Migrate(schema, sourceVersion, payloadElement);
        }

        VerifyIntegrity(root, schema, sourceVersion, documentType!, payloadElement);

        if (sourceVersion == schema.CurrentVersion)
        {
            return DeserializePayload<T>(payloadElement, schema.DocumentType);
        }

        return Migrate(schema, sourceVersion, payloadElement);
    }

    private static async Task<JsonDocument> ParseBoundedAsync(
        Stream source,
        string documentType,
        CancellationToken cancellationToken)
    {
        if (source.CanSeek)
        {
            var remaining = source.Length - source.Position;
            if (remaining < 0)
            {
                throw new InvalidDataException(
                    $"Persisted document '{documentType}' has an invalid stream length.");
            }

            EnsureDocumentSize(documentType, remaining);
            return await JsonDocument.ParseAsync(
                source,
                cancellationToken: cancellationToken);
        }

        // Persisted storage currently reads seekable files, but keep the codec safe if a future
        // caller supplies a non-seekable stream rather than silently losing the size boundary.
        using var buffered = new MemoryStream();
        var buffer = new byte[64 * 1024];
        long total = 0;
        while (true)
        {
            var read = await source.ReadAsync(buffer, cancellationToken);
            if (read == 0)
            {
                break;
            }

            total = checked(total + read);
            EnsureDocumentSize(documentType, total);
            await buffered.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }

        buffered.Position = 0;
        return await JsonDocument.ParseAsync(
            buffered,
            cancellationToken: cancellationToken);
    }

    private static void EnsureDocumentSize(string documentType, long byteSize)
    {
        if (byteSize > MaximumDocumentBytes)
        {
            throw new InvalidDataException(
                $"Persisted document '{documentType}' is {byteSize} bytes and exceeds Steward's {MaximumDocumentBytes}-byte safety ceiling.");
        }
    }

    private static void VerifyIntegrity<T>(
        JsonElement root,
        PersistedDocumentSchema<T> schema,
        int schemaVersion,
        string documentType,
        JsonElement payload)
    {
        var hasIntegrityVersion = root.TryGetProperty("integrityVersion", out var integrityVersionElement);
        var hasContentSha256 = root.TryGetProperty("contentSha256", out var contentSha256Element);

        if (!hasIntegrityVersion && !hasContentSha256)
        {
            if (schemaVersion >= schema.IntegrityRequiredFromVersion)
            {
                throw new InvalidDataException(
                    $"Persisted document schema version {schemaVersion} is missing its required integrity proof.");
            }

            // Older persisted schema versions predate in-envelope integrity metadata.
            return;
        }

        if (!hasIntegrityVersion || !hasContentSha256)
        {
            throw new InvalidDataException(
                "Persisted document integrity proof is incomplete or malformed.");
        }

        if (integrityVersionElement.ValueKind != JsonValueKind.Number ||
            !integrityVersionElement.TryGetInt32(out var integrityVersion) ||
            integrityVersion != CurrentIntegrityVersion)
        {
            throw new InvalidDataException(
                $"Persisted document integrity version is unsupported. Expected {CurrentIntegrityVersion}.");
        }

        if (contentSha256Element.ValueKind != JsonValueKind.String)
        {
            throw new InvalidDataException(
                "Persisted document content SHA-256 digest is missing or malformed.");
        }

        var digestText = contentSha256Element.GetString();
        byte[] expectedHash;
        try
        {
            expectedHash = Convert.FromHexString(digestText ?? string.Empty);
        }
        catch (FormatException exception)
        {
            throw new InvalidDataException(
                "Persisted document content SHA-256 digest is malformed.",
                exception);
        }

        if (expectedHash.Length != SHA256.HashSizeInBytes)
        {
            throw new InvalidDataException(
                "Persisted document content SHA-256 digest has an invalid length.");
        }

        var actualHash = ComputeContentSha256Bytes(documentType, schemaVersion, payload);
        if (!CryptographicOperations.FixedTimeEquals(expectedHash, actualHash))
        {
            throw new InvalidDataException(
                "Persisted document content SHA-256 integrity verification failed.");
        }
    }

    private static string ComputeContentSha256(
        string documentType,
        int schemaVersion,
        JsonElement payload)
        => Convert.ToHexString(ComputeContentSha256Bytes(documentType, schemaVersion, payload));

    private static byte[] ComputeContentSha256Bytes(
        string documentType,
        int schemaVersion,
        JsonElement payload)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("documentType", documentType);
            writer.WriteNumber("schemaVersion", schemaVersion);
            writer.WritePropertyName("payload");
            payload.WriteTo(writer);
            writer.WriteEndObject();
            writer.Flush();
        }

        return SHA256.HashData(buffer.ToArray());
    }

    private static bool LooksLikeEnvelope(JsonElement root)
        => root.ValueKind == JsonValueKind.Object &&
           (root.TryGetProperty("documentType", out _) ||
            root.TryGetProperty("schemaVersion", out _) ||
            root.TryGetProperty("payload", out _));

    private static T Migrate<T>(
        PersistedDocumentSchema<T> schema,
        int sourceVersion,
        JsonElement sourcePayload)
    {
        if (schema.Migrations.TryGetValue(sourceVersion, out var migration))
        {
            return migration(sourcePayload);
        }

        throw new PersistedDataCompatibilityException(
            schema.DocumentType,
            sourceVersion,
            schema.CurrentVersion);
    }

    internal static T DeserializePayload<T>(JsonElement payload, string documentType)
        => payload.Deserialize<T>(JsonOptions)
           ?? throw new InvalidDataException(
               $"Persisted document '{documentType}' contains a null or invalid payload.");
}
