using System.Text.RegularExpressions;

namespace SharedWorlds.Desktop;

internal static partial class DesktopErrorMessage
{
    private const string Redacted = "<redacted>";

    public static string Safe(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return Redact(exception.Message);
    }

    internal static string Redact(string message)
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
