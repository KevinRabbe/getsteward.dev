using System.Text.Json;
using SharedWorlds.Core.Errors;

namespace SharedWorlds.Infrastructure.Storage;

internal sealed record PersistedDocumentEnvelope<T>(
    string DocumentType,
    int SchemaVersion,
    T Payload);

internal sealed record PersistedDocumentSchema<T>(
    string DocumentType,
    int CurrentVersion,
    IReadOnlyDictionary<int, Func<JsonElement, T>> Migrations);

internal static class PersistedDocumentCodec
{
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

        var envelope = new PersistedDocumentEnvelope<T>(
            schema.DocumentType,
            schema.CurrentVersion,
            payload);

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

        using var document = await JsonDocument.ParseAsync(
            source,
            cancellationToken: cancellationToken);

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

        if (sourceVersion == schema.CurrentVersion)
        {
            return DeserializePayload<T>(payloadElement, schema.DocumentType);
        }

        return Migrate(schema, sourceVersion, payloadElement);
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
