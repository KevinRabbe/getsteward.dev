namespace SharedWorlds.Backend.ObjectStorage.S3;

public sealed record S3CompatibleObjectStoreOptions
{
    public S3CompatibleObjectStoreOptions(
        Uri serviceUrl,
        string authenticationRegion,
        string bucketName,
        string accessKeyId,
        string secretAccessKey,
        bool forcePathStyle = false)
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
        var usesLoopbackHttp = string.Equals(
                                   serviceUrl.Scheme,
                                   Uri.UriSchemeHttp,
                                   StringComparison.OrdinalIgnoreCase) &&
                               serviceUrl.IsLoopback;
        if (!usesHttps && !usesLoopbackHttp)
        {
            throw new ArgumentException(
                "S3 service URL must use HTTPS. Plain HTTP is allowed only for loopback development endpoints.",
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
    }

    public Uri ServiceUrl { get; }
    public string AuthenticationRegion { get; }
    public string BucketName { get; }
    public string AccessKeyId { get; }
    public string SecretAccessKey { get; }
    public bool ForcePathStyle { get; }
}
