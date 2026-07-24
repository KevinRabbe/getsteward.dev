using System.Text.Json;

namespace SharedWorlds.Infrastructure.Remote;

internal static class RemoteApiJson
{
    private const long MaximumResponseBytes = 4L * 1024 * 1024;

    public static async Task<T?> DeserializeAsync<T>(
        HttpContent content,
        JsonSerializerOptions options,
        string context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(context);

        if (content.Headers.ContentLength is { } declaredLength)
        {
            EnsureWithinLimit(context, declaredLength);
        }

        await using var source = await content.ReadAsStreamAsync(cancellationToken);
        using var buffered = new MemoryStream();
        var buffer = new byte[64 * 1024];
        long total = 0;

        while (true)
        {
            var read = await source.ReadAsync(buffer, cancellationToken);
            if (read == 0)
            {
                break;
            }

            total = checked(total + read);
            EnsureWithinLimit(context, total);
            await buffered.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }

        buffered.Position = 0;
        try
        {
            return await JsonSerializer.DeserializeAsync<T>(
                buffered,
                options,
                cancellationToken);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException(
                $"Steward returned malformed {context} JSON.",
                exception);
        }
    }

    private static void EnsureWithinLimit(string context, long byteSize)
    {
        if (byteSize > MaximumResponseBytes)
        {
            throw new InvalidDataException(
                $"Steward {context} response is {byteSize} bytes and exceeds the {MaximumResponseBytes}-byte control-response safety ceiling.");
        }
    }
}
