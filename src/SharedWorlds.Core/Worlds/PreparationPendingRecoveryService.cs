using SharedWorlds.Core.Abstractions;
using SharedWorlds.Core.Domain;
using SharedWorlds.Core.Storage;

namespace SharedWorlds.Core.Worlds;

public sealed class PreparationPendingRecoveryException : InvalidOperationException
{
    public PreparationPendingRecoveryException(string code, string message)
        : base(message)
    {
        Code = code;
    }

    public string Code { get; }
}

/// <summary>
/// Reconciles a crash that happened after recovery identity was journaled but before prepared-runtime
/// materialization was confirmed. Only SafeWorld-managed workspaces are automatically discardable:
/// their exact WorkspaceId proves exclusive SafeWorld ownership and no session has started. Native-game
/// identity is preserved and requires an adapter-specific/manual decision because it may name pre-existing state.
/// </summary>
public sealed class PreparationPendingRecoveryService
{
    private readonly IWorkspaceRecoveryStore _recovery;
    private readonly ManagedWorkspaceStorage _managedWorkspaces;

    public PreparationPendingRecoveryService(
        IWorkspaceRecoveryStore recovery,
        ManagedWorkspaceStorage managedWorkspaces)
    {
        ArgumentNullException.ThrowIfNull(recovery);
        ArgumentNullException.ThrowIfNull(managedWorkspaces);
        _recovery = recovery;
        _managedWorkspaces = managedWorkspaces;
    }

    public async Task ResolveManagedAsync(
        WorkspaceId workspaceId,
        CancellationToken cancellationToken = default)
    {
        var record = await LoadPendingAsync(workspaceId, cancellationToken);
        var location = record.RecoveryLocation
            ?? throw new PreparationPendingRecoveryException(
                "RecoveryIdentityMissing",
                "A preparation-pending journal has no stable recovery identity. Safe World will preserve it rather than guess.");
        location.Validate();

        if (location.Kind != PreparedWorldRecoveryLocationKind.SafeWorldManaged)
        {
            throw new PreparationPendingRecoveryException(
                "NativeResolutionRequired",
                "This interrupted preparation uses game-native identity. Safe World will not automatically discard or alter native game state because materialization was never confirmed.");
        }

        var workingDirectory = _managedWorkspaces.GetWorkspaceDirectory(
            record.Id,
            record.AdapterId);
        if (Directory.Exists(workingDirectory))
        {
            try
            {
                _managedWorkspaces.DeleteOwned(
                    record.Id,
                    record.AdapterId,
                    workingDirectory);
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                throw new PreparationPendingRecoveryException(
                    "ManagedCleanupRefused",
                    $"Safe World could not safely discard the interrupted managed preparation: {exception.Message}");
            }
        }

        await _recovery.RemoveAsync(record.Id, cancellationToken);
    }

    private async Task<WorkspaceRecoveryRecord> LoadPendingAsync(
        WorkspaceId workspaceId,
        CancellationToken cancellationToken)
    {
        var records = await _recovery.ListAsync(cancellationToken);
        return records
                   .SingleOrDefault(record =>
                       record.Id == workspaceId &&
                       record.Status == WorkspaceRecoveryStatus.PreparationPending)
               ?? throw new PreparationPendingRecoveryException(
                   "PreparationPendingNotFound",
                   "No interrupted preparation responsibility exists for this exact workspace identity.");
    }
}
