namespace SharedWorlds.Core.Abstractions;

/// <summary>
/// Optional adapter contract for games whose production adapter has validated structured session
/// evidence. Implementing this interface is a capability claim and must be backed by controlled
/// game-specific tests; Core never infers these facts from a process id alone.
/// </summary>
public interface IGameSessionEvidenceProvider
{
    Task<AdapterSessionEvidence> ObserveSessionAsync(
        PreparedWorld world,
        GameSessionHandle session,
        CancellationToken cancellationToken = default);

    Task<AdapterGracefulStopResult> RequestGracefulHostedStopAsync(
        PreparedWorld world,
        GameSessionHandle session,
        CancellationToken cancellationToken = default);

    Task<AdapterSafeCaptureResult> CheckSafeCaptureAsync(
        PreparedWorld world,
        GameSessionHandle session,
        CancellationToken cancellationToken = default);
}

[Flags]
public enum AdapterSessionEvidenceFlags
{
    None = 0,
    LaunchRequested = 1 << 0,
    SessionStarted = 1 << 1,
    HostReady = 1 << 2,
    SessionRunning = 1 << 3,
    GracefulStopRequested = 1 << 4,
    EndedNormally = 1 << 5,
    EndedUnexpectedly = 1 << 6,
    RecoveryEvidencePreserved = 1 << 7
}

public sealed record AdapterSessionEvidence(
    AdapterSessionEvidenceFlags Flags,
    DateTimeOffset ObservedAt,
    string? Detail = null)
{
    public bool Has(AdapterSessionEvidenceFlags fact) => (Flags & fact) == fact;

    public bool ProvesRealSessionStart => Has(AdapterSessionEvidenceFlags.SessionStarted);
    public bool ProvesHostReady => Has(AdapterSessionEvidenceFlags.HostReady);
    public bool ProvesSessionRunning => Has(AdapterSessionEvidenceFlags.SessionRunning);
    public bool ProvesNormalEnd => Has(AdapterSessionEvidenceFlags.EndedNormally);
    public bool ProvesUnexpectedEnd => Has(AdapterSessionEvidenceFlags.EndedUnexpectedly);
}

public enum AdapterGracefulStopStatus
{
    Requested,
    Unsupported,
    Blocked
}

public sealed record AdapterGracefulStopResult(
    AdapterGracefulStopStatus Status,
    string? Reason = null)
{
    public bool StopWasRequested => Status == AdapterGracefulStopStatus.Requested;

    public static AdapterGracefulStopResult Requested()
        => new(AdapterGracefulStopStatus.Requested);

    public static AdapterGracefulStopResult Unsupported(string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        return new(AdapterGracefulStopStatus.Unsupported, reason);
    }

    public static AdapterGracefulStopResult Blocked(string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        return new(AdapterGracefulStopStatus.Blocked, reason);
    }
}

public enum AdapterSafeCaptureStatus
{
    Safe,
    NotYetSafe,
    Blocked,
    Unsupported
}

public sealed record AdapterSafeCaptureResult(
    AdapterSafeCaptureStatus Status,
    string? Reason = null)
{
    public bool IsSafe => Status == AdapterSafeCaptureStatus.Safe;

    public static AdapterSafeCaptureResult Safe()
        => new(AdapterSafeCaptureStatus.Safe);

    public static AdapterSafeCaptureResult NotYetSafe(string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        return new(AdapterSafeCaptureStatus.NotYetSafe, reason);
    }

    public static AdapterSafeCaptureResult Blocked(string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        return new(AdapterSafeCaptureStatus.Blocked, reason);
    }

    public static AdapterSafeCaptureResult Unsupported(string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        return new(AdapterSafeCaptureStatus.Unsupported, reason);
    }
}
