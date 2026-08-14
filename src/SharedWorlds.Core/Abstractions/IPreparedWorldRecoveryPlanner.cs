using SharedWorlds.Core.Domain;
using SharedWorlds.Core.Environment;

namespace SharedWorlds.Core.Abstractions;

/// <summary>
/// Optional adapter capability that declares durable recovery identity before preparation mutates
/// any filesystem or game-owned state. Core journals this plan before invoking the context-aware
/// preparation method, closing the crash window between materialization and recovery registration.
/// </summary>
public interface IPreparedWorldRecoveryPlanner
{
    /// <summary>
    /// Plans the durable location identity for the prepared World without creating, deleting, or
    /// mutating runtime state. A SafeWorld-managed result commits the adapter to the exact
    /// ManagedWorkingDirectory in <paramref name="preparation"/>. A native result must contain only
    /// stable adapter-owned identity, never an absolute path.
    /// </summary>
    Task<PreparedWorldRecoveryLocation> PlanPreparedWorldRecoveryAsync(
        GameInstallation installation,
        EnvironmentManifest requiredEnvironment,
        PreparedWorldPreparationContext preparation,
        CancellationToken cancellationToken = default);
}
