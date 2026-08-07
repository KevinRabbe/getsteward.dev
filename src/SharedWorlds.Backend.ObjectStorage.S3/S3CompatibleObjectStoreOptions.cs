using System.Net;
using System.Net.Sockets;

namespace SharedWorlds.Backend.ObjectStorage.S3;

public sealed record S3CompatibleObjectStoreOptions
{
    public S3CompatibleObjectStoreOptions(
        Uri serviceUrl,
        string authenticationRegion,
        string bucketName,
        string accessKeyId,
        string secretAccessKey,
        bool forcePathStyle = false,
        bool allowInsecurePrivateNetwork = false)
    {
        ArgumentNullException.ThrowIfNull(serviceUrl);
        if (!serviceUrl.IsAbsoluteUri)
        {
            throw new ArgumentException("S3 service URL must be absolute.", nameof(serviceUrl));
        }

        var usesHttps = string.Equals(
            serviceUrl.Scheme,
            Uri.UriSchemeHttps,
            StringComparison.OrdinalIgnoreCase);
        var usesHttp = string.Equals(
            serviceUrl.Scheme,
            Uri.UriSchemeHttp,
            StringComparison.OrdinalIgnoreCase);
        var usesLoopbackHttp = usesHttp && serviceUrl.IsLoopback;
        var usesExplicitPrivateHttp = usesHttp &&
                                      allowInsecurePrivateNetwork &&
                                      IsRfc1918Ipv4Literal(serviceUrl.Host);
        if (!usesHttps && !usesLoopbackHttp && !usesExplicitPrivateHttp)
        {
            throw new ArgumentException(
                "S3 service URL must use HTTPS. Plain HTTP is allowed only for loopback development endpoints or, when explicitly enabled, an RFC1918 IPv4 literal on a private service network.",
                nameof(serviceUrl));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(authenticationRegion);
        ArgumentException.ThrowIfNullOrWhiteSpace(bucketName);
        ArgumentException.ThrowIfNullOrWhiteSpace(accessKeyId);
        ArgumentException.ThrowIfNullOrWhiteSpace(secretAccessKey);

        ServiceUrl = serviceUrl;
        AuthenticationRegion = authenticationRegion;
        BucketName = bucketName;
        AccessKeyId = accessKeyId;
        SecretAccessKey = secretAccessKey;
        ForcePathStyle = forcePathStyle;
        AllowInsecurePrivateNetwork = allowInsecurePrivateNetwork;
    }

    public Uri ServiceUrl { get; }
    public string AuthenticationRegion { get; }
    public string BucketName { get; }
    public string AccessKeyId { get; }
    public string SecretAccessKey { get; }
    public bool ForcePathStyle { get; }
    public bool AllowInsecurePrivateNetwork { get; }

    private static bool IsRfc1918Ipv4Literal(string host)
    {
        if (!IPAddress.TryParse(host, out var address) ||
            address.AddressFamily != AddressFamily.InterNetwork)
        {
            return false;
        }

        var bytes = address.GetAddressBytes();
        return bytes[0] == 10 ||
               (bytes[0] == 172 && bytes[1] is >= 16 and <= 31) ||
               (bytes[0] == 192 && bytes[1] == 168);
    }
}
