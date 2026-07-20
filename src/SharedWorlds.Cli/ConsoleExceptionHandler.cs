using System.Text;
using System.Text.Json;
using SharedWorlds.Core.Errors;

namespace SharedWorlds.Cli;

internal static class ConsoleExceptionHandler
{
    public static async Task<int> HandleAsync(
        Exception exception,
        string diagnosticsRoot)
    {
        ArgumentNullException.ThrowIfNull(exception);
        ArgumentException.ThrowIfNullOrWhiteSpace(diagnosticsRoot);

        switch (exception)
        {
            case OperationCanceledException:
                Console.Error.WriteLine("Operation cancelled. The lifecycle stopped through normal cancellation handling. If a game session had already started, check recovery before retrying.");
                return ApplicationExitCodes.Cancelled;

            case WorldNotFoundException worldNotFound:
                WriteExpected(
                    "World not found",
                    worldNotFound.Message,
                    "The World may have been removed, moved, or the wrong World ID may have been supplied.");
                return ApplicationExitCodes.ProductFailure;

            case RevisionNotFoundException revisionNotFound:
                WriteExpected(
                    "World revision missing",
                    revisionNotFound.Message,
                    "The canonical World was not repaired automatically. Check recovery data before changing or deleting anything.");
                return ApplicationExitCodes.ProductFailure;

            case WorldIntegrityException integrity:
                WriteExpected(
                    "World integrity problem",
                    integrity.Message,
                    "The last known-good canonical state is preserved. Check the recovery list and stored revisions before retrying.");
                return ApplicationExitCodes.ProductFailure;

            case AdapterMismatchException mismatch:
                WriteExpected(
                    "Wrong game adapter",
                    mismatch.Message,
                    "Open this World with the adapter that originally created it.");
                return ApplicationExitCodes.ProductFailure;

            case PersistedDataCompatibilityException compatibility:
                WriteExpected(
                    "Stored data is not compatible with this build",
                    compatibility.Message,
                    "Use a build that supports this schema or update the application. The stored data was not rewritten.");
                return ApplicationExitCodes.ProductFailure;

            case EnvironmentReproductionException environmentFailure:
                WriteExpected(
                    "Required game environment is unavailable",
                    environmentFailure.Message,
                    "Restore the required game/mod environment or use a future Verify/Repair workflow. The canonical World was not changed.");
                return ApplicationExitCodes.ProductFailure;

            case WorldSharingRequiredException sharingRequired:
                WriteExpected(
                    "Sharing is not enabled",
                    sharingRequired.Message,
                    "Explicitly enable sharing for this World before using Host or Join. Local Continue remains available without sharing.");
                return ApplicationExitCodes.ProductFailure;

            case WorldSessionConflictException sessionConflict:
                WriteExpected(
                    "World is already active",
                    sessionConflict.Message,
                    "Join the current host, request handoff, or wait until the canonical session ends.");
                return ApplicationExitCodes.ProductFailure;

            case SharedWorldsException productFailure:
                WriteExpected(
                    "World operation failed",
                    productFailure.Message,
                    "The operation stopped at a controlled product boundary.");
                return ApplicationExitCodes.ProductFailure;

            case FileNotFoundException fileNotFound:
                return await ReportOperationalFailureAsync(
                    "Required file not found",
                    "A required game, save, workspace, or stored-state file could not be found. It may have moved or been deleted after discovery.",
                    fileNotFound,
                    diagnosticsRoot);

            case UnauthorizedAccessException accessDenied:
                return await ReportOperationalFailureAsync(
                    "Access denied",
                    "SharedWorlds could not access a required file or directory. Check filesystem permissions and whether another program is locking the path.",
                    accessDenied,
                    diagnosticsRoot);

            case JsonException invalidJson:
                return await ReportOperationalFailureAsync(
                    "Stored metadata could not be read",
                    "A metadata document is malformed or incomplete. The application stopped instead of guessing how to interpret it.",
                    invalidJson,
                    diagnosticsRoot);

            case InvalidDataException invalidData:
                return await ReportOperationalFailureAsync(
                    "Stored data is invalid",
                    "A persisted document or state package failed validation. The canonical World was not repaired automatically.",
                    invalidData,
                    diagnosticsRoot);

            case IOException ioFailure:
                return await ReportOperationalFailureAsync(
                    "File or storage operation failed",
                    "A filesystem or storage operation failed. Canonical state only advances after durable revision storage succeeds.",
                    ioFailure,
                    diagnosticsRoot);

            default:
                return await ReportUnexpectedFailureAsync(exception, diagnosticsRoot);
        }
    }

    private static void WriteExpected(string title, string message, string hint)
    {
        Console.Error.WriteLine($"{title}: {message}");
        Console.Error.WriteLine($"Next step: {hint}");
    }

    private static async Task<int> ReportOperationalFailureAsync(
        string title,
        string userMessage,
        Exception exception,
        string diagnosticsRoot)
    {
        var incident = await TryWriteDiagnosticAsync(exception, diagnosticsRoot);

        Console.Error.WriteLine($"{title}: {userMessage}");
        WriteDiagnosticReference(incident);
        return ApplicationExitCodes.FileSystemFailure;
    }

    private static async Task<int> ReportUnexpectedFailureAsync(
        Exception exception,
        string diagnosticsRoot)
    {
        var incident = await TryWriteDiagnosticAsync(exception, diagnosticsRoot);

        Console.Error.WriteLine("Unexpected application failure. The operation has been stopped rather than continuing in an unknown state.");
        WriteDiagnosticReference(incident);
        return ApplicationExitCodes.UnexpectedFailure;
    }

    private static void WriteDiagnosticReference(DiagnosticIncident incident)
    {
        Console.Error.WriteLine($"Incident ID: {incident.Id}");
        if (incident.LogPath is not null)
        {
            Console.Error.WriteLine($"Diagnostic log: {incident.LogPath}");
        }
        else
        {
            Console.Error.WriteLine("The diagnostic log could not be written. The incident ID can still be used to correlate the failure in this session.");
        }
    }

    private static async Task<DiagnosticIncident> TryWriteDiagnosticAsync(
        Exception exception,
        string diagnosticsRoot)
    {
        var incidentId = Guid.NewGuid().ToString("N")[..12];
        var timestamp = DateTimeOffset.UtcNow;
        var logPath = Path.Combine(diagnosticsRoot, $"errors-{timestamp:yyyy-MM-dd}.log");

        try
        {
            Directory.CreateDirectory(diagnosticsRoot);

            var entry = new StringBuilder()
                .AppendLine("================================================================================")
                .AppendLine($"TimestampUtc: {timestamp:O}")
                .AppendLine($"IncidentId: {incidentId}")
                .AppendLine($"ProcessId: {Environment.ProcessId}")
                .AppendLine($"OS: {Environment.OSVersion}")
                .AppendLine($"Runtime: {Environment.Version}")
                .AppendLine(exception.ToString())
                .AppendLine()
                .ToString();

            await File.AppendAllTextAsync(
                logPath,
                entry,
                Encoding.UTF8,
                CancellationToken.None);
            return new DiagnosticIncident(incidentId, logPath);
        }
        catch (Exception loggingFailure) when (
            loggingFailure is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return new DiagnosticIncident(incidentId, null);
        }
    }

    private sealed record DiagnosticIncident(string Id, string? LogPath);
}
