namespace SharedWorlds.Cli;

internal static class ApplicationExitCodes
{
    public const int Success = 0;
    public const int UsageError = 2;
    public const int ProductFailure = 10;
    public const int FileSystemFailure = 20;
    public const int UnexpectedFailure = 70;
    public const int Cancelled = 130;
}
