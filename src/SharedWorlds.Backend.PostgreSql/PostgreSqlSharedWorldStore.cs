using Npgsql;
using SharedWorlds.Backend.Identity;
using SharedWorlds.Backend.Worlds;
using SharedWorlds.Core.Domain;

namespace SharedWorlds.Backend.PostgreSql;

public sealed partial class PostgreSqlSharedWorldStore :
    ISharedWorldMetadataStore,
    ISharedRevisionMetadataStore
{
    private readonly NpgsqlDataSource _dataSource;

    public PostgreSqlSharedWorldStore(NpgsqlDataSource dataSource)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        _dataSource = dataSource;
    }

    private static SharedWorldMetadata ReadWorld(NpgsqlDataReader reader)
    {
        var environmentOrdinal = reader.GetOrdinal("current_environment_revision_id");
        return new SharedWorldMetadata(
            new WorldId(reader.GetGuid(reader.GetOrdinal("world_id"))),
            reader.GetString(reader.GetOrdinal("adapter_id")),
            reader.GetString(reader.GetOrdinal("display_name")),
            new RevisionId(reader.GetGuid(reader.GetOrdinal("current_state_revision_id"))),
            reader.IsDBNull(environmentOrdinal)
                ? null
                : new RevisionId(reader.GetGuid(environmentOrdinal)),
            new ExternalIdentityRef(
                reader.GetString(reader.GetOrdinal("access_manager_provider")),
                reader.GetString(reader.GetOrdinal("access_manager_external_id"))),
            reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("created_at")),
            reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("updated_at")));
    }

    private static SharedWorldMember ReadMember(NpgsqlDataReader reader)
        => new(
            new WorldId(reader.GetGuid(reader.GetOrdinal("world_id"))),
            new ExternalIdentityRef(
                reader.GetString(reader.GetOrdinal("provider")),
                reader.GetString(reader.GetOrdinal("external_id"))),
            (SharedWorldMemberStatus)reader.GetInt16(reader.GetOrdinal("status")),
            reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("added_at")));

    private static SharedStateRevisionMetadata ReadStateRevision(NpgsqlDataReader reader)
    {
        var environmentOrdinal = reader.GetOrdinal("required_environment_revision_id");
        return new SharedStateRevisionMetadata(
            new WorldId(reader.GetGuid(reader.GetOrdinal("world_id"))),
            new RevisionId(reader.GetGuid(reader.GetOrdinal("revision_id"))),
            reader.GetString(reader.GetOrdinal("adapter_id")),
            reader.GetString(reader.GetOrdinal("package_object_key")),
            reader.GetInt64(reader.GetOrdinal("byte_size")),
            reader.GetString(reader.GetOrdinal("sha256")),
            reader.IsDBNull(environmentOrdinal)
                ? null
                : new RevisionId(reader.GetGuid(environmentOrdinal)),
            new ExternalIdentityRef(
                reader.GetString(reader.GetOrdinal("published_by_provider")),
                reader.GetString(reader.GetOrdinal("published_by_external_id"))),
            reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("published_at")));
    }

    private static SharedEnvironmentRevisionMetadata ReadEnvironmentRevision(NpgsqlDataReader reader)
    {
        var byteSizeOrdinal = reader.GetOrdinal("byte_size");
        var shaOrdinal = reader.GetOrdinal("sha256");
        return new SharedEnvironmentRevisionMetadata(
            new WorldId(reader.GetGuid(reader.GetOrdinal("world_id"))),
            new RevisionId(reader.GetGuid(reader.GetOrdinal("revision_id"))),
            reader.GetString(reader.GetOrdinal("adapter_id")),
            reader.GetString(reader.GetOrdinal("artifact_reference")),
            reader.IsDBNull(byteSizeOrdinal) ? null : reader.GetInt64(byteSizeOrdinal),
            reader.IsDBNull(shaOrdinal) ? null : reader.GetString(shaOrdinal),
            new ExternalIdentityRef(
                reader.GetString(reader.GetOrdinal("published_by_provider")),
                reader.GetString(reader.GetOrdinal("published_by_external_id"))),
            reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("published_at")));
    }
}
