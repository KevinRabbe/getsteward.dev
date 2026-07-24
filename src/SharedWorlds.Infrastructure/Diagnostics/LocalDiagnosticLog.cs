using System.Text;
using System.Text.RegularExpressions;

namespace SharedWorlds.Infrastructure.Diagnostics;

public sealed record LocalDiagnosticIncident(string Id, string? LogPath);

public static partial class LocalDiagnosticLog
{
    private const string Redacted = "<redacted>";
    private const string TruncatedMarker = "<diagnostic-details-truncated>";
    private const int MaximumDetailCharacters = 64 * 1024;
    private const int MaximumIncidentFiles = 64;
    private const int MaximumInnerExceptions = 8;

    public static LocalDiagnosticIncident TryWriteException(Exception exception, string diagnosticsRoot)
    {
        ArgumentNullException.ThrowIfNull(exception);
        ArgumentException.ThrowIfNullOrWhiteSpace(diagnosticsRoot);

        var incidentId = Guid.NewGuid().ToString("N")[..12];
        var timestamp = DateTimeOffset.UtcNow;

        try
        {
            Directory.CreateDirectory(diagnosticsRoot);
            DeleteLegacyUnredactedLogs(diagnosticsRoot);
            PruneOldIncidentFiles(diagnosticsRoot, keepNewest: MaximumIncidentFiles - 1);

            var details = BuildBoundedDetails(exception);
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

    private static string BuildBoundedDetails(Exception exception)
    {
        var builder = new StringBuilder(capacity: 4096);
        Exception? current = exception;
        var depth = 0;
        var truncated = false;

        while (current is not null && depth < MaximumInnerExceptions && !truncated)
        {
            if (depth > 0)
            {
                truncated = !TryAppendBounded(builder, Environment.NewLine + "--- inner exception ---" + Environment.NewLine);
            }

            if (!truncated)
            {
                truncated = !TryAppendBounded(
                    builder,
                    current.GetType().FullName ?? current.GetType().Name);
            }

            if (!truncated)
            {
                truncated = !TryAppendBounded(builder, ": ");
            }

            if (!truncated)
            {
                var message = BoundInputSegment(current.Message, out var messageWasTruncated);
                truncated = !TryAppendBounded(builder, Redact(message)) || messageWasTruncated;
            }

            if (!truncated && !string.IsNullOrWhiteSpace(current.StackTrace))
            {
                var stackTrace = BoundInputSegment(current.StackTrace, out var stackWasTruncated);
                truncated = !TryAppendBounded(
                    builder,
                    Environment.NewLine + Redact(stackTrace)) || stackWasTruncated;
            }

            current = current.InnerException;
            depth++;
        }

        if (current is not null)
        {
            truncated = true;
        }

        if (truncated)
        {
            AppendTruncationMarker(builder);
        }

        return builder.ToString();
    }

    private static string BoundInputSegment(string? value, out bool truncated)
    {
        if (string.IsNullOrEmpty(value))
        {
            truncated = false;
            return string.Empty;
        }

        if (value.Length <= MaximumDetailCharacters)
        {
            truncated = false;
            return value;
        }

        truncated = true;
        return value[..MaximumDetailCharacters];
    }

    private static bool TryAppendBounded(StringBuilder builder, string value)
    {
        var reserved = Environment.NewLine.Length + TruncatedMarker.Length;
        var maximumContentCharacters = MaximumDetailCharacters - reserved;
        var available = maximumContentCharacters - builder.Length;
        if (available <= 0)
        {
            return false;
        }

        if (value.Length <= available)
        {
            builder.Append(value);
            return true;
        }

        builder.Append(value.AsSpan(0, available));
        return false;
    }

    private static void AppendTruncationMarker(StringBuilder builder)
    {
        var reserved = Environment.NewLine.Length + TruncatedMarker.Length;
        var maximumContentCharacters = MaximumDetailCharacters - reserved;
        if (builder.Length > maximumContentCharacters)
        {
            builder.Length = maximumContentCharacters;
        }

        builder.AppendLine();
        builder.Append(TruncatedMarker);
    }

    private static void DeleteLegacyUnredactedLogs(string diagnosticsRoot)
    {
        foreach (var path in Directory.EnumerateFiles(
                     diagnosticsRoot,
                     "errors-*.log",
                     SearchOption.TopDirectoryOnly))
        {
            TryDelete(path);
        }
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
            TryDelete(file.FullName);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
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
