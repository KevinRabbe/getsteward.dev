using Npgsql;
using SharedWorlds.Backend.Identity;
using SharedWorlds.Backend.Worlds;
using SharedWorlds.Core.Domain;

namespace SharedWorlds.Backend.PostgreSql;

public sealed class PostgreSqlSharedWorldReservationAbandonStore : ISharedWorldReservationAbandonStore
{
    private readonly NpgsqlDataSource _dataSource;

    public PostgreSqlSharedWorldReservationAbandonStore(NpgsqlDataSource dataSource)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        _dataSource = dataSource;
    }

    public async Task<AbandonSharedWorldReservationStatus> AbandonAsync(
        ExternalIdentityRef caller,
        WorldId worldId,
        string installationId,
        Guid sessionId,
        long generation,
        CancellationToken cancellationToken = default)
    {
        const string sql = """
            DELETE FROM steward_world_reservations
            WHERE world_id = @world_id
              AND session_id = @session_id
              AND generation = @generation
              AND holder_provider = @holder_provider
              AND holder_external_id = @holder_external_id
              AND installation_id = @installation_id;
            """;

        await using var command = _dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("world_id", worldId.Value);
        command.Parameters.AddWithValue("session_id", sessionId);
        command.Parameters.AddWithValue("generation", generation);
        command.Parameters.AddWithValue("holder_provider", caller.Provider);
        command.Parameters.AddWithValue("holder_external_id", caller.ExternalId);
        command.Parameters.AddWithValue("installation_id", installationId);
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1
            ? AbandonSharedWorldReservationStatus.Abandoned
            : AbandonSharedWorldReservationStatus.NoLongerCurrent;
    }
}
