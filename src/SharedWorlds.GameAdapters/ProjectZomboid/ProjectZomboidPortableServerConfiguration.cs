using System.Text;

namespace SharedWorlds.GameAdapters.ProjectZomboid;

internal static class ProjectZomboidPortableServerConfiguration
{
    private static readonly byte[] Utf8Bom = [0xEF, 0xBB, 0xBF];
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);
    private static readonly HashSet<string> LocalSecretKeys = new(
        ["RCONPassword", "DiscordToken"],
        StringComparer.OrdinalIgnoreCase);

    internal static byte[] Sanitize(byte[] sourceBytes)
    {
        ArgumentNullException.ThrowIfNull(sourceBytes);

        var hasBom = sourceBytes.AsSpan().StartsWith(Utf8Bom);
        var content = hasBom
            ? sourceBytes.AsSpan(Utf8Bom.Length)
            : sourceBytes.AsSpan();

        string sourceText;
        try
        {
            sourceText = StrictUtf8.GetString(content);
        }
        catch (DecoderFallbackException exception)
        {
            throw new InvalidDataException(
                "Project Zomboid server configuration is not valid UTF-8; Steward will not risk copying unknown credential bytes into portable state.",
                exception);
        }

        var sanitizedText = SanitizeText(sourceText);
        var sanitizedContent = StrictUtf8.GetBytes(sanitizedText);
        if (!hasBom)
        {
            return sanitizedContent;
        }

        var result = new byte[Utf8Bom.Length + sanitizedContent.Length];
        Utf8Bom.CopyTo(result, 0);
        sanitizedContent.CopyTo(result.AsSpan(Utf8Bom.Length));
        return result;
    }

    internal static async Task SanitizeFileAsync(
        string path,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var sourceBytes = await File.ReadAllBytesAsync(path, cancellationToken);
        var sanitizedBytes = Sanitize(sourceBytes);
        if (sourceBytes.AsSpan().SequenceEqual(sanitizedBytes))
        {
            return;
        }

        await File.WriteAllBytesAsync(path, sanitizedBytes, cancellationToken);
    }

    internal static string SanitizeText(string sourceText)
    {
        ArgumentNullException.ThrowIfNull(sourceText);
        var builder = new StringBuilder(sourceText.Length);
        var offset = 0;
        while (offset < sourceText.Length)
        {
            var newlineIndex = sourceText.IndexOf('\n', offset);
            var end = newlineIndex >= 0 ? newlineIndex : sourceText.Length;
            var lineLength = end - offset;
            var line = sourceText.AsSpan(offset, lineLength);
            var contentLength = lineLength > 0 && line[^1] == '\r'
                ? lineLength - 1
                : lineLength;
            var content = line[..contentLength];
            var equalsIndex = content.IndexOf('=');

            if (equalsIndex >= 0 &&
                LocalSecretKeys.Contains(content[..equalsIndex].Trim().ToString()))
            {
                builder.Append(content[..(equalsIndex + 1)]);
            }
            else
            {
                builder.Append(content);
            }

            if (contentLength != lineLength)
            {
                builder.Append('\r');
            }

            if (newlineIndex >= 0)
            {
                builder.Append('\n');
                offset = newlineIndex + 1;
            }
            else
            {
                offset = sourceText.Length;
            }
        }

        return builder.ToString();
    }
}
