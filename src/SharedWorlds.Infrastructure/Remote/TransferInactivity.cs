namespace SharedWorlds.Infrastructure.Remote;

public sealed class RemoteTransferInactivityTimeoutException : IOException
{
    public RemoteTransferInactivityTimeoutException(TimeSpan inactivityTimeout)
        : base($"Remote package transfer made no progress for {inactivityTimeout}.")
    {
        InactivityTimeout = inactivityTimeout;
    }

    public TimeSpan InactivityTimeout { get; }
}

internal static class TransferInactivity
{
    public static async Task<HttpResponseMessage> SendForHeadersAsync(
        HttpClient client,
        HttpRequestMessage request,
        HttpCompletionOption completionOption,
        TimeSpan inactivityTimeout,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(request);
        ValidateTimeout(inactivityTimeout);

        using var inactivityCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        inactivityCancellation.CancelAfter(inactivityTimeout);
        try
        {
            return await client.SendAsync(
                request,
                completionOption,
                inactivityCancellation.Token);
        }
        catch (OperationCanceledException) when (
            !cancellationToken.IsCancellationRequested &&
            inactivityCancellation.IsCancellationRequested)
        {
            throw new RemoteTransferInactivityTimeoutException(inactivityTimeout);
        }
    }

    public static async ValueTask<int> ReadAsync(
        Stream source,
        Memory<byte> buffer,
        TimeSpan inactivityTimeout,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        ValidateTimeout(inactivityTimeout);

        using var inactivityCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        inactivityCancellation.CancelAfter(inactivityTimeout);
        try
        {
            return await source.ReadAsync(buffer, inactivityCancellation.Token);
        }
        catch (OperationCanceledException) when (
            !cancellationToken.IsCancellationRequested &&
            inactivityCancellation.IsCancellationRequested)
        {
            throw new RemoteTransferInactivityTimeoutException(inactivityTimeout);
        }
    }

    private static void ValidateTimeout(TimeSpan inactivityTimeout)
    {
        if (inactivityTimeout <= TimeSpan.Zero || inactivityTimeout == Timeout.InfiniteTimeSpan)
        {
            throw new ArgumentOutOfRangeException(nameof(inactivityTimeout));
        }
    }
}
