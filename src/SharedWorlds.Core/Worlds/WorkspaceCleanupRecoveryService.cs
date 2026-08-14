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
/// Resolves cleanup-only prepared-runtime responsibility after canonical state handling is already
/// finished. Runtime location is reconstructed from durable recovery identity; legacy absolute paths
/// are interpreted only inside PreparedWorldRecoveryResolver.
/// </summary>
public sealed class WorkspaceCleanupRecoveryService
{
    private readonly IWorldStorage _storage;
    private readonly IWorkspaceRecoveryStore _recovery;
    private readonly PreparedWorldRecoveryResolver _resolver;

    public WorkspaceCleanupRecoveryService(
        IWorldStorage storage,
        IWorkspaceRecoveryStore recovery,
        PreparedWorldRecoveryResolver resolver)
    {
        ArgumentNullException.ThrowIfNull(storage);
        ArgumentNullException.ThrowIfNull(recovery);
        ArgumentNullException.ThrowIfNull(resolver);
        _storage = storage;
        _recovery = recovery;
        _resolver = resolver;
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
                "The pending prepared-runtime cleanup belongs to a different game adapter.");
        }

        var pathWithoutInstallation = _resolver.ResolveWorkingDirectoryWithoutInstallation(
            record,
            adapter.Id);
        if (pathWithoutInstallation is not null && !Directory.Exists(pathWithoutInstallation))
        {
            await _recovery.RemoveAsync(record.Id, cancellationToken);
            return;
        }

        if (installation is null)
        {
            throw new WorkspaceCleanupRecoveryException(
                "InstallationRequired",
                "The pending prepared runtime requires a current local game installation for safe adapter cleanup.");
        }

        var environmentId = record.EnvironmentRevisionId
            ?? throw new WorkspaceCleanupRecoveryException(
                "EnvironmentUnknown",
                "This older cleanup record does not identify the exact environment that created the prepared runtime. Safe World will preserve it rather than guess.");
        var environment = await _storage.LoadEnvironmentRevisionAsync(
            worldId,
            environmentId,
            cancellationToken)
            ?? throw new WorkspaceCleanupRecoveryException(
                "EnvironmentMissing",
                "The exact World environment metadata required to finalize this prepared runtime is unavailable.");
        if (!string.Equals(environment.Manifest.AdapterId, adapter.Id, StringComparison.Ordinal))
        {
            throw new WorkspaceCleanupRecoveryException(
                "EnvironmentAdapterMismatch",
                "The journaled World environment belongs to a different game adapter.");
        }

        PreparedWorld prepared;
        try
        {
            prepared = _resolver.Resolve(
                record,
                adapter,
                installation,
                environment.Manifest);
        }
        catch (Exception exception) when (
            exception is InvalidDataException or InvalidOperationException or ArgumentException)
        {
            throw new WorkspaceCleanupRecoveryException(
                "RuntimeResolutionFailed",
                $"Safe World could not reconstruct the prepared runtime from its durable identity: {exception.Message}");
        }

        if (Directory.Exists(prepared.WorkingDirectory))
        {
            await adapter.FinalizePreparedWorldAsync(
                prepared,
                PreparedWorldDisposition.Discard,
                cancellationToken);
        }

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
                   "No cleanup-only prepared-runtime responsibility exists for this World.");
    }
}
