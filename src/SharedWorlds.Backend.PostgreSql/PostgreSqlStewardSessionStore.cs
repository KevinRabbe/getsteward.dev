using Npgsql;
using NpgsqlTypes;
using SharedWorlds.Backend.Identity;

namespace SharedWorlds.Backend.PostgreSql;

public sealed class PostgreSqlStewardSessionStore : IStewardSessionStore
{
    private readonly NpgsqlDataSource _dataSource;

    public PostgreSqlStewardSessionStore(NpgsqlDataSource dataSource)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        _dataSource = dataSource;
    }

    public async Task ReplaceInstallationSessionAsync(
        StewardSessionRecord session,
        StewardAccessCredentialRecord accessCredential,
        DateTimeOffset replacedAt,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(accessCredential);
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await LockInstallationAsync(
            connection,
            transaction,
            session.InstallationId,
            cancellationToken);

        const string revokeSql = """
            UPDATE steward_auth_sessions
            SET revoked = true,
                revoked_at = @revoked_at
            WHERE installation_id = @installation_id
              AND revoked = false;
            """;
        await using (var revokeCommand = new NpgsqlCommand(revokeSql, connection, transaction))
        {
            revokeCommand.Parameters.AddWithValue("revoked_at", replacedAt);
            revokeCommand.Parameters.AddWithValue("installation_id", session.InstallationId);
            await revokeCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        const string sessionSql = """
            INSERT INTO steward_auth_sessions (
                session_id,
                identity_provider,
                identity_external_id,
                display_name,
                installation_id,
                refresh_token_hash,
                created_at,
                refresh_expires_at,
                revoked,
                revoked_at)
            VALUES (
                @session_id,
                @identity_provider,
                @identity_external_id,
                @display_name,
                @installation_id,
                @refresh_token_hash,
                @created_at,
                @refresh_expires_at,
                @revoked,
                @revoked_at);
            """;
        await using (var sessionCommand = new NpgsqlCommand(sessionSql, connection, transaction))
        {
            sessionCommand.Parameters.AddWithValue("session_id", session.Id.Value);
            sessionCommand.Parameters.AddWithValue("identity_provider", session.Identity.Subject.Provider);
            sessionCommand.Parameters.AddWithValue("identity_external_id", session.Identity.Subject.ExternalId);
            sessionCommand.Parameters.Add(new NpgsqlParameter("display_name", NpgsqlDbType.Text)
            {
                Value = session.Identity.DisplayName is { } displayName ? displayName : DBNull.Value
            });
            sessionCommand.Parameters.AddWithValue("installation_id", session.InstallationId);
            sessionCommand.Parameters.AddWithValue("refresh_token_hash", session.RefreshTokenHash);
            sessionCommand.Parameters.AddWithValue("created_at", session.CreatedAt);
            sessionCommand.Parameters.AddWithValue("refresh_expires_at", session.RefreshExpiresAt);
            sessionCommand.Parameters.AddWithValue("revoked", session.Revoked);
            sessionCommand.Parameters.Add(new NpgsqlParameter("revoked_at", NpgsqlDbType.TimestampTz)
            {
                Value = session.RevokedAt is { } revokedAt ? revokedAt : DBNull.Value
            });
            await sessionCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        await InsertAccessCredentialAsync(
            connection,
            transaction,
            accessCredential,
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<StewardAccessContext?> LoadAccessContextAsync(
        string accessTokenHash,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accessTokenHash);
        const string sql = """
            SELECT
                s.session_id,
                s.identity_provider,
                s.identity_external_id,
                s.display_name,
                s.installation_id,
                s.refresh_token_hash,
                s.created_at,
                s.refresh_expires_at,
                s.revoked,
                s.revoked_at,
                a.access_token_hash,
                a.expires_at
            FROM steward_access_credentials a
            INNER JOIN steward_auth_sessions s
                ON s.session_id = a.session_id
            WHERE a.access_token_hash = @access_token_hash;
            """;
        await using var command = _dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("access_token_hash", accessTokenHash);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        var session = ReadSession(reader);
        var access = new StewardAccessCredentialRecord(
            reader.GetString(reader.GetOrdinal("access_token_hash")),
            session.Id,
            reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("expires_at")));
        return new StewardAccessContext(session, access);
    }

    public async Task<StewardSessionRecord?> LoadRefreshSessionAsync(
        string refreshTokenHash,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(refreshTokenHash);
        const string sql = """
            SELECT
                session_id,
                identity_provider,
                identity_external_id,
                display_name,
                installation_id,
                refresh_token_hash,
                created_at,
                refresh_expires_at,
                revoked,
                revoked_at
            FROM steward_auth_sessions
            WHERE refresh_token_hash = @refresh_token_hash;
            """;
        await using var command = _dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("refresh_token_hash", refreshTokenHash);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadSession(reader) : null;
    }

    public async Task<StoreRotateRefreshSessionStatus> TryRotateRefreshSessionAsync(
        StewardSessionId sessionId,
        string expectedRefreshTokenHash,
        string installationId,
        string newRefreshTokenHash,
        DateTimeOffset newRefreshExpiresAt,
        StewardAccessCredentialRecord newAccessCredential,
        DateTimeOffset rotatedAt,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedRefreshTokenHash);
        ArgumentException.ThrowIfNullOrWhiteSpace(installationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(newRefreshTokenHash);
        ArgumentNullException.ThrowIfNull(newAccessCredential);

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await LockInstallationAsync(connection, transaction, installationId, cancellationToken);

        const string updateSql = """
            UPDATE steward_auth_sessions
            SET refresh_token_hash = @new_refresh_token_hash,
                refresh_expires_at = @new_refresh_expires_at
            WHERE session_id = @session_id
              AND refresh_token_hash = @expected_refresh_token_hash
              AND installation_id = @installation_id
              AND revoked = false
              AND refresh_expires_at > @rotated_at;
            """;
        await using (var updateCommand = new NpgsqlCommand(updateSql, connection, transaction))
        {
            updateCommand.Parameters.AddWithValue("new_refresh_token_hash", newRefreshTokenHash);
            updateCommand.Parameters.AddWithValue("new_refresh_expires_at", newRefreshExpiresAt);
            updateCommand.Parameters.AddWithValue("session_id", sessionId.Value);
            updateCommand.Parameters.AddWithValue("expected_refresh_token_hash", expectedRefreshTokenHash);
            updateCommand.Parameters.AddWithValue("installation_id", installationId);
            updateCommand.Parameters.AddWithValue("rotated_at", rotatedAt);
            if (await updateCommand.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                await transaction.RollbackAsync(cancellationToken);
                return StoreRotateRefreshSessionStatus.InvalidCredential;
            }
        }

        await InsertAccessCredentialAsync(
            connection,
            transaction,
            newAccessCredential,
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return StoreRotateRefreshSessionStatus.Rotated;
    }

    public async Task<bool> TryRevokeRefreshSessionAsync(
        string refreshTokenHash,
        string installationId,
        DateTimeOffset revokedAt,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(refreshTokenHash);
        ArgumentException.ThrowIfNullOrWhiteSpace(installationId);
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await LockInstallationAsync(connection, transaction, installationId, cancellationToken);

        const string sql = """
            UPDATE steward_auth_sessions
            SET revoked = true,
                revoked_at = @revoked_at
            WHERE refresh_token_hash = @refresh_token_hash
              AND installation_id = @installation_id
              AND revoked = false;
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("revoked_at", revokedAt);
        command.Parameters.AddWithValue("refresh_token_hash", refreshTokenHash);
        command.Parameters.AddWithValue("installation_id", installationId);
        var changed = await command.ExecuteNonQueryAsync(cancellationToken) == 1;
        await transaction.CommitAsync(cancellationToken);
        return changed;
    }

    private static async Task LockInstallationAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string installationId,
        CancellationToken cancellationToken)
    {
        const string sql = "SELECT pg_advisory_xact_lock(hashtextextended(@installation_id, 0));";
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("installation_id", installationId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task InsertAccessCredentialAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        StewardAccessCredentialRecord accessCredential,
        CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO steward_access_credentials (
                access_token_hash,
                session_id,
                expires_at)
            VALUES (
                @access_token_hash,
                @session_id,
                @expires_at);
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("access_token_hash", accessCredential.AccessTokenHash);
        command.Parameters.AddWithValue("session_id", accessCredential.SessionId.Value);
        command.Parameters.AddWithValue("expires_at", accessCredential.ExpiresAt);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static StewardSessionRecord ReadSession(NpgsqlDataReader reader)
    {
        var displayNameOrdinal = reader.GetOrdinal("display_name");
        var revokedAtOrdinal = reader.GetOrdinal("revoked_at");
        return new StewardSessionRecord(
            new StewardSessionId(reader.GetGuid(reader.GetOrdinal("session_id"))),
            new VerifiedExternalIdentity(
                new ExternalIdentityRef(
                    reader.GetString(reader.GetOrdinal("identity_provider")),
                    reader.GetString(reader.GetOrdinal("identity_external_id"))),
                reader.IsDBNull(displayNameOrdinal)
                    ? null
                    : reader.GetString(displayNameOrdinal)),
            reader.GetString(reader.GetOrdinal("installation_id")),
            reader.GetString(reader.GetOrdinal("refresh_token_hash")),
            reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("created_at")),
            reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("refresh_expires_at")),
            reader.GetBoolean(reader.GetOrdinal("revoked")),
            reader.IsDBNull(revokedAtOrdinal)
                ? null
                : reader.GetFieldValue<DateTimeOffset>(revokedAtOrdinal));
    }
}
