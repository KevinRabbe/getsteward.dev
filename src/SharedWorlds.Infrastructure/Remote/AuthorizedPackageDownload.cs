namespace SharedWorlds.Infrastructure.Remote;

public sealed record AuthorizedPackageDownload
{
    public AuthorizedPackageDownload(
        Uri uri,
        IReadOnlyDictionary<string, string> requiredHeaders,
        DateTimeOffset expiresAt,
        long expectedByteSize,
        string expectedSha256)
    {
        ArgumentNullException.ThrowIfNull(uri);
        ArgumentNullException.ThrowIfNull(requiredHeaders);
        if (!StewardRemoteEndpointPolicy.IsAllowedHttpEndpoint(uri))
        {
            throw new ArgumentException(
                "Package download URI must use HTTPS. Plain HTTP is allowed only for loopback development endpoints.",
                nameof(uri));
        }

        if (expiresAt <= DateTimeOffset.UtcNow)
        {
            throw new ArgumentOutOfRangeException(nameof(expiresAt));
        }

        if (expectedByteSize <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(expectedByteSize));
        }

        if (!IsValidSha256(expectedSha256))
        {
            throw new ArgumentException(
                "Expected SHA-256 must be exactly 64 hexadecimal characters.",
                nameof(expectedSha256));
        }

        Uri = uri;
        RequiredHeaders = new Dictionary<string, string>(requiredHeaders, StringComparer.OrdinalIgnoreCase);
        ExpiresAt = expiresAt;
        ExpectedByteSize = expectedByteSize;
        ExpectedSha256 = expectedSha256.ToUpperInvariant();
    }

    public Uri Uri { get; }
    public IReadOnlyDictionary<string, string> RequiredHeaders { get; }
    public DateTimeOffset ExpiresAt { get; }
    public long ExpectedByteSize { get; }
    public string ExpectedSha256 { get; }

    private static bool IsValidSha256(string value)
        => !string.IsNullOrWhiteSpace(value) &&
           value.Length == 64 &&
           value.All(Uri.IsHexDigit);
}

public sealed record VerifiedCachedPackage(
    string Path,
    long ByteSize,
    string Sha256);
