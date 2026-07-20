namespace SharedWorlds.Core.Environment;

public enum EnvironmentVerificationState
{
    Ready = 0,
    Blocked = 1,
    Unsupported = 2
}

public sealed record EnvironmentVerificationIssue(
    string Code,
    string Message,
    bool CanRepairAutomatically = false);

public sealed record EnvironmentVerificationReport(
    EnvironmentVerificationState State,
    IReadOnlyList<EnvironmentVerificationIssue> Issues)
{
    public bool IsReady => State == EnvironmentVerificationState.Ready;

    public bool CanRepairAutomatically =>
        State == EnvironmentVerificationState.Blocked &&
        Issues.Any(issue => issue.CanRepairAutomatically);

    public static EnvironmentVerificationReport Ready()
        => new(EnvironmentVerificationState.Ready, []);

    public static EnvironmentVerificationReport Blocked(params EnvironmentVerificationIssue[] issues)
        => new(EnvironmentVerificationState.Blocked, issues);

    public static EnvironmentVerificationReport Unsupported(string message)
        => new(
            EnvironmentVerificationState.Unsupported,
            [new EnvironmentVerificationIssue("environment-verification-unsupported", message)]);
}

public sealed record EnvironmentRepairResult(
    bool Changed,
    EnvironmentVerificationReport Verification,
    string Message);
