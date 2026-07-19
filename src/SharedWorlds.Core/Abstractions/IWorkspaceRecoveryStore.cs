using SharedWorlds.Core.Domain;

namespace SharedWorlds.Core.Abstractions;

/// <summary>
/// Durable registry for prepared workspaces that may contain recoverable state.
/// An Active record left behind after process restart is treated as an interrupted-session candidate.
/// </summary>
public interface IWorkspaceRecoveryStore
{
    Task SaveAsync(
        WorkspaceRecoveryRecord record,
        CancellationToken cancellationToken = default);

    Task RemoveAsync(
        WorkspaceId workspaceId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<WorkspaceRecoveryRecord>> ListAsync(
        CancellationToken cancellationToken = default);
}
