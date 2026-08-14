using SharedWorlds.Core.Abstractions;
using SharedWorlds.Core.Domain;
using SharedWorlds.Core.Storage;

namespace SharedWorlds.Core.Worlds;

/// <summary>
/// Separates game-specific prepared-runtime finalization from SafeWorld-managed filesystem ownership.
/// Adapters release/persist game-specific runtime resources; Core alone proves and deletes a managed
/// workspace root by the exact WorkspaceId recorded in the durable recovery journal.
/// </summary>
public sealed class PreparedWorldFinalizationCoordinator
{
    private readonly ManagedWorkspaceStorage _managedWorkspaces;

    public PreparedWorldFinalizationCoordinator(ManagedWorkspaceStorage managedWorkspaces)
    {
        ArgumentNullException.ThrowIfNull(managedWorkspaces);
        _managedWorkspaces = managedWorkspaces;
    }

    public async Task FinalizeAsync(
        WorkspaceRecoveryRecord record,
        IGameAdapter adapter,
        PreparedWorld preparedWorld,
        PreparedWorldDisposition disposition,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(adapter);
        ArgumentNullException.ThrowIfNull(preparedWorld);

        if (!string.Equals(record.AdapterId, adapter.Id, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Recovery workspace '{record.Id}' belongs to adapter '{record.AdapterId}', not '{adapter.Id}'.");
        }

        var recoveryLocation = record.RecoveryLocation;
        if (recoveryLocation is not null)
        {
            recoveryLocation.Validate();
            var preparedLocation = preparedWorld.RecoveryLocation
                ?? throw new InvalidOperationException(
                    "A descriptor-based recovery record resolved to a prepared runtime without recovery identity.");
            preparedLocation.Validate();
            if (!PreparedWorldRecoveryPlanPolicy.LocationsEqual(
                    recoveryLocation,
                    preparedLocation))
            {
                throw new InvalidOperationException(
                    "Prepared runtime recovery identity does not match the durable recovery journal.");
            }
        }

        var isManaged = recoveryLocation?.Kind ==
            PreparedWorldRecoveryLocationKind.SafeWorldManaged;
        if (isManaged)
        {
            // Prove exact WorkspaceId -> path ownership before adapter code can touch the runtime.
            _managedWorkspaces.RequireOwned(
                record.Id,
                adapter.Id,
                preparedWorld.WorkingDirectory);
        }

        var adapterDisposition = isManaged &&
                                 disposition == PreparedWorldDisposition.Discard
            ? PreparedWorldDisposition.ReleaseForCoreManagedDiscard
            : disposition;

        await adapter.FinalizePreparedWorldAsync(
            preparedWorld,
            adapterDisposition,
            cancellationToken);

        if (isManaged && disposition == PreparedWorldDisposition.Discard)
        {
            _managedWorkspaces.DeleteOwned(
                record.Id,
                adapter.Id,
                preparedWorld.WorkingDirectory);
        }
    }
}
