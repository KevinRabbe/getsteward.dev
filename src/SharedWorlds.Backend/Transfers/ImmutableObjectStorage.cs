namespace SharedWorlds.Backend.Transfers;

public sealed record ImmutableStoredObject(
    string ObjectKey,
    long ByteSize,
    string Sha256);

/// <summary>
/// ProviderUploadId is an opaque durable provider handle, not necessarily the provider's raw upload
/// identifier. It must contain or encode every provider-specific value needed to resume, list,
/// complete, or abort the upload after a backend process restart. For S3-compatible storage that
/// means the handle may encode both the object key and native multipart upload ID.
/// </summary>
public sealed record ImmutableUploadSession(
    string ProviderUploadId,
    string ObjectKey);

public sealed record ImmutableUploadedPart(
    int PartNumber,
    long ByteSize);

public sealed record ImmutableUploadSnapshot(
    string ProviderUploadId,
    string ObjectKey,
    IReadOnlyList<ImmutableUploadedPart> CompletedParts,
    bool IsCompleted);

public sealed record DirectObjectTransferAuthorization(
    Uri Uri,
    string Method,
    IReadOnlyDictionary<string, string> RequiredHeaders,
    DateTimeOffset ExpiresAt,
    long ExpectedByteSize);

/// <summary>
/// Provider-neutral private immutable object-storage boundary.
/// Implementations may use S3-compatible or equivalent storage, but must keep objects private and
/// issue only narrowly scoped short-lived direct-transfer authorization.
///
/// Provider upload handles must remain sufficient after process restart; implementations must not
/// depend on volatile process-local upload-ID mappings.
///
/// Exact object verification is part of this contract: returned SHA-256 values must represent the
/// bytes actually stored, not an unverified checksum supplied by the desktop client.
/// </summary>
public interface IPrivateImmutableObjectStore
{
    Task<ImmutableUploadSession> BeginMultipartUploadAsync(
        string objectKey,
        long expectedByteSize,
        string expectedSha256,
        CancellationToken cancellationToken = default);

    Task<ImmutableUploadSnapshot?> GetMultipartUploadAsync(
        string providerUploadId,
        CancellationToken cancellationToken = default);

    Task<DirectObjectTransferAuthorization> AuthorizeUploadPartAsync(
        string providerUploadId,
        int partNumber,
        long expectedByteSize,
        DateTimeOffset expiresAt,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Completes the provider upload idempotently and returns independently verified stored-object
    /// integrity metadata. Repeating this after a successful completion must return the same object.
    /// </summary>
    Task<ImmutableStoredObject> CompleteMultipartUploadAsync(
        string providerUploadId,
        CancellationToken cancellationToken = default);

    Task AbortMultipartUploadAsync(
        string providerUploadId,
        CancellationToken cancellationToken = default);

    Task<ImmutableStoredObject?> InspectObjectAsync(
        string objectKey,
        CancellationToken cancellationToken = default);

    Task<DirectObjectTransferAuthorization> AuthorizeDownloadAsync(
        string objectKey,
        long expectedByteSize,
        DateTimeOffset expiresAt,
        CancellationToken cancellationToken = default);

    Task DeleteObjectAsync(
        string objectKey,
        CancellationToken cancellationToken = default);
}
