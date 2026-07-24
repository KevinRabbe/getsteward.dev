namespace SharedWorlds.Infrastructure.Remote;

/// <summary>
/// Enforces the transport boundary for direct immutable package GET/PUT requests.
/// Production uses the parameterless constructor, whose inner HTTP handler never follows redirects.
/// </summary>
public sealed class StewardDirectTransferHandler : DelegatingHandler
{
    public StewardDirectTransferHandler()
        : base(new HttpClientHandler
        {
            AllowAutoRedirect = false
        })
    {
    }

    public StewardDirectTransferHandler(HttpMessageHandler innerHandler)
        : base(innerHandler ?? throw new ArgumentNullException(nameof(innerHandler)))
    {
    }

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!StewardRemoteEndpointPolicy.IsAllowedHttpEndpoint(request.RequestUri))
        {
            throw new HttpRequestException(
                "Direct package transfer URI must use HTTPS. Plain HTTP is allowed only for loopback development endpoints.");
        }

        return base.SendAsync(request, cancellationToken);
    }
}
