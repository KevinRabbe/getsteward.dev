using SharedWorlds.Infrastructure.Diagnostics;

namespace SharedWorlds.Desktop;

internal static class DesktopErrorMessage
{
    public static string Safe(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return Redact(exception.Message);
    }

    internal static string Redact(string message)
        => LocalDiagnosticLog.Redact(message);
}
