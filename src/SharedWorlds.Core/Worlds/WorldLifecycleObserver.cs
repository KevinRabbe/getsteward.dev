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
