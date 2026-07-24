namespace SharedWorlds.Infrastructure.Remote;

public static class StewardRemoteEndpointPolicy
{
    public static bool TryNormalizeApiBaseAddress(
        Uri? apiBaseAddress,
        out Uri? normalizedBaseAddress)
    {
        normalizedBaseAddress = null;
        if (apiBaseAddress is null || !apiBaseAddress.IsAbsoluteUri)
        {
            return false;
        }

        var usesHttps = string.Equals(
            apiBaseAddress.Scheme,
            Uri.UriSchemeHttps,
            StringComparison.OrdinalIgnoreCase);
        var usesLoopbackHttp = string.Equals(
                                   apiBaseAddress.Scheme,
                                   Uri.UriSchemeHttp,
                                   StringComparison.OrdinalIgnoreCase) &&
                               apiBaseAddress.IsLoopback;
        if (!usesHttps && !usesLoopbackHttp)
        {
            return false;
        }

        var absolute = apiBaseAddress.AbsoluteUri;
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
