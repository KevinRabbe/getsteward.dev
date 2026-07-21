using Npgsql;
using NpgsqlTypes;
using SharedWorlds.Backend.Worlds;
using SharedWorlds.Core.Domain;

namespace SharedWorlds.Backend.PostgreSql;

public sealed partial class PostgreSqlSharedWorldStore
{
    public async Task<StoreRevisionMetadataStatus> TryRecordStateRevisionAsync(
        SharedStateRevisionMetadata revision,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(revision);
        const string sql = """
            INSERT INTO steward_state_revisions (
                world_id,
                revision_id,
                adapter_id,
                package_object_key,
                byte_size,
                sha256,
                required_environment_revision_id,
                published_by_provider,
                published_by_external_id,
                published_at)
            VALUES (
                @world_id,
                @revision_id,
                @adapter_id,
                @package_object_key,
                @byte_size,
                @sha256,
                @required_environment_revision_id,
                @published_by_provider,
                @published_by_external_id,
                @published_at)
            ON CONFLICT (world_id, revision_id) DO NOTHING;
            """;

        await using var command = _dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("world_id", revision.WorldId.Value);
        command.Parameters.AddWithValue("revision_id", revision.RevisionId.Value);
        command.Parameters.AddWithValue("adapter_id", revision.AdapterId);
        command.Parameters.AddWithValue("package_object_key", revision.PackageObjectKey);
        command.Parameters.AddWithValue("byte_size", revision.ByteSize);
        command.Parameters.AddWithValue("sha256", revision.Sha256.ToUpperInvariant());
        command.Parameters.Add(new NpgsqlParameter("required_environment_revision_id", NpgsqlDbType.Uuid)
        {
            Value = revision.RequiredEnvironmentRevisionId is { } environmentRevision
                ? environmentRevision.Value
                : DBNull.Value
        });
        command.Parameters.AddWithValue("published_by_provider", revision.PublishedBy.Provider);
        command.Parameters.AddWithValue("published_by_external_id", revision.PublishedBy.ExternalId);
        command.Parameters.AddWithValue("published_at", revision.PublishedAt);

        if (await command.ExecuteNonQueryAsync(cancellationToken) == 1)
        {
            return StoreRevisionMetadataStatus.Recorded;
        }

        var existing = await LoadStateRevisionAsync(
            revision.WorldId,
            revision.RevisionId,
            cancellationToken);
        return existing is not null && SameStateArtifact(existing, revision)
            ? StoreRevisionMetadataStatus.AlreadyRecorded
            : StoreRevisionMetadataStatus.Conflict;
    }

    public async Task<StoreRevisionMetadataStatus> TryRecordEnvironmentRevisionAsync(
        SharedEnvironmentRevisionMetadata revision,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(revision);
        const string sql = """
            INSERT INTO steward_environment_revisions (
                world_id,
                revision_id,
                adapter_id,
                artifact_reference,
                byte_size,
                sha256,
                published_by_provider,
                published_by_external_id,
                published_at)
            VALUES (
                @world_id,
                @revision_id,
                @adapter_id,
                @artifact_reference,
                @byte_size,
                @sha256,
                @published_by_provider,
                @published_by_external_id,
                @published_at)
            ON CONFLICT (world_id, revision_id) DO NOTHING;
            """;

        await using var command = _dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("world_id", revision.WorldId.Value);
        command.Parameters.AddWithValue("revision_id", revision.RevisionId.Value);
        command.Parameters.AddWithValue("adapter_id", revision.AdapterId);
        command.Parameters.AddWithValue("artifact_reference", revision.ArtifactReference);
        command.Parameters.Add(new NpgsqlParameter("byte_size", NpgsqlDbType.Bigint)
        {
            Value = revision.ByteSize is { } byteSize ? byteSize : DBNull.Value
        });
        command.Parameters.Add(new NpgsqlParameter("sha256", NpgsqlDbType.Text)
        {
            Value = revision.Sha256 is { } sha256 ? sha256.ToUpperInvariant() : DBNull.Value
        });
        command.Parameters.AddWithValue("published_by_provider", revision.PublishedBy.Provider);
        command.Parameters.AddWithValue("published_by_external_id", revision.PublishedBy.ExternalId);
        command.Parameters.AddWithValue("published_at", revision.PublishedAt);

        if (await command.ExecuteNonQueryAsync(cancellationToken) == 1)
        {
            return StoreRevisionMetadataStatus.Recorded;
        }

        var existing = await LoadEnvironmentRevisionAsync(
            revision.WorldId,
            revision.RevisionId,
            cancellationToken);
        return existing is not null && SameEnvironmentArtifact(existing, revision)
            ? StoreRevisionMetadataStatus.AlreadyRecorded
            : StoreRevisionMetadataStatus.Conflict;
    }

    public async Task<SharedStateRevisionMetadata?> LoadStateRevisionAsync(
        WorldId worldId,
        RevisionId revisionId,
        CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT
                world_id,
                revision_id,
                adapter_id,
                package_object_key,
                byte_size,
                sha256,
                required_environment_revision_id,
                published_by_provider,
                published_by_external_id,
                published_at
            FROM steward_state_revisions
            WHERE world_id = @world_id
              AND revision_id = @revision_id;
            """;

        await using var command = _dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("world_id", worldId.Value);
        command.Parameters.AddWithValue("revision_id", revisionId.Value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadStateRevision(reader) : null;
    }

    public async Task<SharedEnvironmentRevisionMetadata?> LoadEnvironmentRevisionAsync(
        WorldId worldId,
        RevisionId revisionId,
        CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT
                world_id,
                revision_id,
                adapter_id,
                artifact_reference,
                byte_size,
                sha256,
                published_by_provider,
                published_by_external_id,
                published_at
            FROM steward_environment_revisions
            WHERE world_id = @world_id
              AND revision_id = @revision_id;
            """;

        await using var command = _dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("world_id", worldId.Value);
        command.Parameters.AddWithValue("revision_id", revisionId.Value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadEnvironmentRevision(reader) : null;
    }

    private static bool SameStateArtifact(
        SharedStateRevisionMetadata left,
        SharedStateRevisionMetadata right)
        => left.WorldId == right.WorldId &&
           left.RevisionId == right.RevisionId &&
           string.Equals(left.AdapterId, right.AdapterId, StringComparison.Ordinal) &&
           string.Equals(left.PackageObjectKey, right.PackageObjectKey, StringComparison.Ordinal) &&
           left.ByteSize == right.ByteSize &&
           string.Equals(left.Sha256, right.Sha256, StringComparison.OrdinalIgnoreCase) &&
           left.RequiredEnvironmentRevisionId == right.RequiredEnvironmentRevisionId &&
           left.PublishedBy == right.PublishedBy;

    private static bool SameEnvironmentArtifact(
        SharedEnvironmentRevisionMetadata left,
        SharedEnvironmentRevisionMetadata right)
        => left.WorldId == right.WorldId &&
           left.RevisionId == right.RevisionId &&
           string.Equals(left.AdapterId, right.AdapterId, StringComparison.Ordinal) &&
           string.Equals(left.ArtifactReference, right.ArtifactReference, StringComparison.Ordinal) &&
           left.ByteSize == right.ByteSize &&
           string.Equals(left.Sha256, right.Sha256, StringComparison.OrdinalIgnoreCase) &&
           left.PublishedBy == right.PublishedBy;
}
