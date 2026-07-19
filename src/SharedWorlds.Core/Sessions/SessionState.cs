namespace SharedWorlds.Core.Sessions;

public enum SessionState
{
    Available,
    Preparing,
    Hosting,
    HandoffRequested,
    Committing,
    Synchronizing,
    RecoveryPending
}
