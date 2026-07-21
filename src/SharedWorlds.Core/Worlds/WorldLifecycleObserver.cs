using SharedWorlds.Core.Domain;

namespace SharedWorlds.Core.Worlds;

public enum ManagedWorldSessionMode
{
    Local,
    Hosted
}

public enum WorldLifecyclePhase
{
    ResolvingWorld,
    AcquiringReservation,
    DownloadingState,
    PreparingEnvironment,
    RestoringState,
    RegisteringRecovery,
    StartingSession,
    Running,
    WaitingForSafeCapture,
    Capturing,
    StoringCandidate,
    Committing,
    Finalizing,
    Completed,
    RecoveryNeeded,
    CleanupPending
}

public sealed record WorldLifecyclePhaseChange(
    WorldId WorldId,
    ManagedWorldSessionMode? Mode,
    WorldLifecyclePhase Phase,
    DateTimeOffset ObservedAt,
    string? Detail = null);

/// <summary>
/// Receives non-authoritative lifecycle projections for presentation/runtime status. An observer
/// must not own session authority, storage, capture, or commit behavior, and implementations must
/// not allow presentation failures to escape back into the lifecycle transaction.
/// </summary>
public interface IWorldLifecycleObserver
{
    void OnPhaseChanged(WorldLifecyclePhaseChange change);
}

public sealed class NullWorldLifecycleObserver : IWorldLifecycleObserver
{
    public static NullWorldLifecycleObserver Instance { get; } = new();

    private NullWorldLifecycleObserver()
    {
    }

    public void OnPhaseChanged(WorldLifecyclePhaseChange change)
    {
        ArgumentNullException.ThrowIfNull(change);
    }
}
