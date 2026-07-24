using System.Text;
using System.Text.RegularExpressions;

namespace SharedWorlds.Infrastructure.Diagnostics;

public sealed record LocalDiagnosticIncident(string Id, string? LogPath);

public static partial class LocalDiagnosticLog
{
    private const string Redacted = "<redacted>";
    private const int MaximumDetailCharacters = 64 * 1024;
    private const int MaximumIncidentFiles = 64;

    public static LocalDiagnosticIncident TryWriteException(Exception exception, string diagnosticsRoot)
    {
        ArgumentNullException.ThrowIfNull(exception);
        ArgumentException.ThrowIfNullOrWhiteSpace(diagnosticsRoot);

        var incidentId = Guid.NewGuid().ToString("N")[..12];
        var timestamp = DateTimeOffset.UtcNow;

        try
        {
            Directory.CreateDirectory(diagnosticsRoot);
            PruneOldIncidentFiles(diagnosticsRoot, keepNewest: MaximumIncidentFiles - 1);

            var details = Redact(exception.ToString());
            if (details.Length > MaximumDetailCharacters)
            {
                details = details[..MaximumDetailCharacters] + Environment.NewLine + "<diagnostic-details-truncated>";
            }

            var logPath = Path.Combine(
                diagnosticsRoot,
                $"error-{timestamp:yyyyMMdd-HHmmssfff}-{incidentId}.log");
            var entry = new StringBuilder()
                .AppendLine($"TimestampUtc: {timestamp:O}")
                .AppendLine($"IncidentId: {incidentId}")
                .AppendLine($"ProcessId: {Environment.ProcessId}")
                .AppendLine($"OS: {Environment.OSVersion}")
                .AppendLine($"Runtime: {Environment.Version}")
                .AppendLine($"ExceptionType: {exception.GetType().FullName}")
                .AppendLine("Details:")
                .AppendLine(details)
                .ToString();

            File.WriteAllText(logPath, entry, Encoding.UTF8);
            return new LocalDiagnosticIncident(incidentId, logPath);
        }
        catch (Exception loggingFailure) when (
            loggingFailure is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return new LocalDiagnosticIncident(incidentId, null);
        }
    }

    public static string Redact(string message)
    {
        if (string.IsNullOrEmpty(message))
        {
            return message;
        }

        var redacted = BearerTokenRegex().Replace(message, $"$1{Redacted}");
        redacted = SecretAssignmentRegex().Replace(redacted, match =>
            $"{match.Groups[1].Value}{Redacted}");
        redacted = UrlQueryRegex().Replace(redacted, match =>
            $"{match.Groups[1].Value}?{Redacted}{match.Groups[3].Value}");
        return redacted;
    }

    private static void PruneOldIncidentFiles(string diagnosticsRoot, int keepNewest)
    {
        var files = Directory.EnumerateFiles(
                diagnosticsRoot,
                "error-*.log",
                SearchOption.TopDirectoryOnly)
            .Select(path => new FileInfo(path))
            .OrderByDescending(file => file.LastWriteTimeUtc)
            .ThenByDescending(file => file.Name, StringComparer.Ordinal)
            .Skip(Math.Max(0, keepNewest))
            .ToArray();

        foreach (var file in files)
        {
            try
            {
                file.Delete();
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    [GeneratedRegex(
        @"(?i)(\b(?:authorization\s*:\s*)?bearer\s+)[^\s,;]+",
        RegexOptions.CultureInvariant)]
    private static partial Regex BearerTokenRegex();

    [GeneratedRegex(
        """(?i)(\b(?:password|adminpassword|serverpassword|access[_-]?token|refresh[_-]?token|join[_-]?token|session[_-]?token|steam[_-]?(?:ticket|auth[_-]?ticket)|(?:web[_-]?)?api[_-]?key|publisher[_-]?(?:api[_-]?)?key|token|secret)\b\s*[\"']?\s*[=:]\s*)(?:\"[^\"]*\"|'[^']*'|[^\s,;}\]]+)""",
        RegexOptions.CultureInvariant)]
    private static partial Regex SecretAssignmentRegex();

    [GeneratedRegex(
        @"(https?://[^\s?]+)\?([^\s#]*)(#[^\s]*)?",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex UrlQueryRegex();
}
