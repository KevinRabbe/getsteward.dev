namespace SharedWorlds.Infrastructure.Remote;

public static class StewardRemoteEndpointPolicy
{
    public static bool IsAllowedHttpEndpoint(Uri? endpoint)
    {
        if (endpoint is null || !endpoint.IsAbsoluteUri)
        {
            return false;
        }

        if (string.Equals(
                endpoint.Scheme,
                Uri.UriSchemeHttps,
                StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return string.Equals(
                   endpoint.Scheme,
                   Uri.UriSchemeHttp,
                   StringComparison.OrdinalIgnoreCase) &&
               endpoint.IsLoopback;
    }

    public static bool TryNormalizeApiBaseAddress(
        Uri? apiBaseAddress,
        out Uri? normalizedBaseAddress)
    {
        normalizedBaseAddress = null;
        if (!IsAllowedHttpEndpoint(apiBaseAddress))
        {
            return false;
        }

        var absolute = apiBaseAddress!.AbsoluteUri;
        normalizedBaseAddress = absolute.EndsWith("/", StringComparison.Ordinal)
            ? apiBaseAddress
            : new Uri(absolute + '/', UriKind.Absolute);
        return true;
    }

    public static Uri NormalizeApiBaseAddress(Uri apiBaseAddress)
    {
        ArgumentNullException.ThrowIfNull(apiBaseAddress);
        if (TryNormalizeApiBaseAddress(apiBaseAddress, out var normalized) && normalized is not null)
        {
            return normalized;
        }

        throw new ArgumentException(
            "Steward API base address must use HTTPS. Plain HTTP is allowed only for loopback development endpoints.",
            nameof(apiBaseAddress));
    }
}
