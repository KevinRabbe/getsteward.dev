using Amazon.Runtime;
using Amazon.S3;

namespace SharedWorlds.Backend.ObjectStorage.S3;

public static class S3CompatibleObjectStoreFactory
{
    public static RecoveringS3CompatibleImmutableObjectStore Create(S3CompatibleObjectStoreOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var config = new AmazonS3Config
        {
            ServiceURL = options.ServiceUrl.AbsoluteUri.TrimEnd('/'),
            AuthenticationRegion = options.AuthenticationRegion,
            ForcePathStyle = options.ForcePathStyle
        };
        var credentials = new BasicAWSCredentials(options.AccessKeyId, options.SecretAccessKey);
        var client = new AmazonS3Client(credentials, config);
        var protocol = options.ServiceUrl.Scheme == Uri.UriSchemeHttp
            ? Protocol.HTTP
            : Protocol.HTTPS;
        return new RecoveringS3CompatibleImmutableObjectStore(
            client,
            options.BucketName,
            protocol,
            ownsClient: true);
    }
}
