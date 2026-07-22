using SharedWorlds.Core.Abstractions;
using SharedWorlds.Core.Domain;

namespace SharedWorlds.Core.Worlds;

public sealed class WorkspaceCleanupRecoveryException : InvalidOperationException
{
    public WorkspaceCleanupRecoveryException(string code, string message)
        : base(message)
    {
        Code = code;
    }

    public string Code { get; }
}

/// <summary>
/// Resolves cleanup-only workspace responsibility after canonical state handling is already finished.
/// This service never captures state, publishes a revision, changes a World head, or acquires writable
/// authority. Adapter-owned cleanup remains adapter-owned; Core only reconstructs the exact prepared-
/// workspace context required to retry that cleanup and removes the durable journal after cleanup succeeds.
/// </summary>
public sealed class WorkspaceCleanupRecoveryService
{
    private readonly IWorldStorage _storage;
    private readonly IWorkspaceRecoveryStore _recovery;

    public WorkspaceCleanupRecoveryService(
        IWorldStorage storage,
        IWorkspaceRecoveryStore recovery)
    {
        ArgumentNullException.ThrowIfNull(storage);
        ArgumentNullException.ThrowIfNull(recovery);
        _storage = storage;
        _recovery = recovery;
    }

    public async Task RetryAsync(
        WorldId worldId,
        IGameAdapter adapter,
        GameInstallation? installation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(adapter);

        var record = await LoadCleanupRecordAsync(worldId, cancellationToken);
        if (!string.Equals(record.AdapterId, adapter.Id, StringComparison.Ordinal))
        {
            throw new WorkspaceCleanupRecoveryException(
                "AdapterMismatch",
                "The pending workspace cleanup belongs to a different game adapter.");
        }

        if (Directory.Exists(record.WorkingDirectory))
        {
            if (installation is null)
            {
                throw new WorkspaceCleanupRecoveryException(
                    "InstallationRequired",
                    "The preserved workspace still exists, so the game adapter requires a local installation to finish cleanup safely.");
            }

            var environmentId = record.EnvironmentRevisionId
                ?? throw new WorkspaceCleanupRecoveryException(
                    "EnvironmentUnknown",
                    "This older cleanup record does not identify the exact environment that created the workspace. Steward will preserve the workspace rather than guess.");
            var environment = await _storage.LoadEnvironmentRevisionAsync(
                worldId,
                environmentId,
                cancellationToken)
                ?? throw new WorkspaceCleanupRecoveryException(
                    "EnvironmentMissing",
                    "The exact World environment metadata required to finalize this workspace is unavailable.");
            if (!string.Equals(environment.Manifest.AdapterId, adapter.Id, StringComparison.Ordinal))
            {
                throw new WorkspaceCleanupRecoveryException(
                    "EnvironmentAdapterMismatch",
                    "The journaled World environment belongs to a different game adapter.");
            }

            var prepared = new PreparedWorld(
                installation,
                record.WorkingDirectory,
                environment.Manifest);
            await adapter.FinalizePreparedWorldAsync(
                prepared,
                PreparedWorldDisposition.Discard,
                cancellationToken);
        }

        // If the workspace is already gone, a previous cleanup succeeded and only journal removal is
        // left. If adapter cleanup just succeeded above, the same rule applies. A failed removal is
        // allowed to propagate so the durable cleanup responsibility remains visible and retryable.
        await _recovery.RemoveAsync(record.Id, cancellationToken);
    }

    private async Task<WorkspaceRecoveryRecord> LoadCleanupRecordAsync(
        WorldId worldId,
        CancellationToken cancellationToken)
    {
        var records = await _recovery.ListAsync(cancellationToken);
        return records
                   .Where(record =>
                       record.WorldId == worldId &&
                       record.Status == WorkspaceRecoveryStatus.CleanupPending)
                   .OrderBy(record => record.CreatedAt)
                   .ThenBy(record => record.Id.ToString(), StringComparer.Ordinal)
                   .FirstOrDefault()
               ?? throw new WorkspaceCleanupRecoveryException(
                   "CleanupNotFound",
                   "No cleanup-only workspace responsibility exists for this World.");
    }
}
