using Npgsql;
using NpgsqlTypes;
using SharedWorlds.Core.Domain;
using SharedWorlds.Core.Worlds;

namespace SharedWorlds.Backend.PostgreSql;

/// <summary>
/// Bounded owner-scoped read model for private World discovery. Mutation and CAS authority remain in
/// <see cref="PostgreSqlOwnedWorldLocationStore"/>.
/// </summary>
public sealed class PostgreSqlOwnedWorldLocationCatalogStore :
    IOwnedWorldLocationCatalogStore
{
    private readonly NpgsqlDataSource _dataSource;

    public PostgreSqlOwnedWorldLocationCatalogStore(NpgsqlDataSource dataSource)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        _dataSource = dataSource;
    }

    public async Task<IReadOnlyList<OwnedWorldLocationClaim>> ListOwnerWorldLocationsAsync(
        string ownerProvider,
        string ownerExternalId,
        int maximumClaims,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerProvider);
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerExternalId);
        if (maximumClaims is < 1 or > OwnedPrivateWorldCatalogService.MaximumClaims)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumClaims));
        }

        const string sql = """
            SELECT world_id,
                   owner_provider,
                   owner_external_id,
                   installation_id,
                   state_revision_id,
                   environment_revision_id,
                   observed_at,
                   world_name,
                   game_adapter_id
              FROM steward_owned_world_locations
             WHERE owner_provider = @owner_provider
               AND owner_external_id = @owner_external_id
             ORDER BY world_id ASC, observed_at DESC, installation_id ASC
             LIMIT @limit;
            """;

        await using var command = _dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("owner_provider", NpgsqlDbType.Text, ownerProvider);
        command.Parameters.AddWithValue("owner_external_id", NpgsqlDbType.Text, ownerExternalId);
        command.Parameters.AddWithValue("limit", NpgsqlDbType.Integer, maximumClaims + 1);

        var claims = new List<OwnedWorldLocationClaim>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var worldNameIsNull = reader.IsDBNull(7);
            var gameAdapterIdIsNull = reader.IsDBNull(8);
            if (worldNameIsNull != gameAdapterIdIsNull)
            {
                throw new InvalidDataException(
                    "A persisted owned-World catalog claim contains only one side of its presentation metadata.");
            }

            claims.Add(new OwnedWorldLocationClaim(
                new WorldId(reader.GetGuid(0)),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                new RevisionId(reader.GetGuid(4)),
                new RevisionId(reader.GetGuid(5)),
                reader.GetFieldValue<DateTimeOffset>(6))
            {
                Presentation = worldNameIsNull
                    ? null
                    : new OwnedWorldPresentation(
                        reader.GetString(7),
                        reader.GetString(8))
            });
        }

        return claims;
    }
}
