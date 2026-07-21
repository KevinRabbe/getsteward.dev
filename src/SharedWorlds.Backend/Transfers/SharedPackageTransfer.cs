using SharedWorlds.Backend.Identity;
using SharedWorlds.Core.Domain;

namespace SharedWorlds.Backend.Transfers;

public readonly record struct SharedPackageTransferId(Guid Value)
{
    public static SharedPackageTransferId New() => new(Guid.NewGuid());
    public override string ToString() => Value.ToString("N");
}

public enum SharedPackageKind
{
    State,
    Environment
}

public enum SharedPackageTransferState
{
    Active,
    Finalized,
    IntegrityFailed,
    PublicationConflict,
    Abandoned
}

public sealed record SharedPackageTransferRecord(
    SharedPackageTransferId Id,
    WorldId WorldId,
    RevisionId RevisionId,
    SharedPackageKind Kind,
    string AdapterId,
    ExternalIdentityRef Owner,
    string ObjectKey,
    string ProviderUploadId,
    long ExpectedByteSize,
    string ExpectedSha256,
    RevisionId? RequiredEnvironmentRevisionId,
    int PartSizeBytes,
    int PartCount,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt,
    SharedPackageTransferState State,
    DateTimeOffset? FinalizedAt = null);

public interface ISharedPackageTransferStore
{
    Task<bool> TryCreateAsync(
        SharedPackageTransferRecord transfer,
        CancellationToken cancellationToken = default);

    Task<SharedPackageTransferRecord?> LoadAsync(
        SharedPackageTransferId transferId,
        CancellationToken cancellationToken = default);

    Task<bool> TrySetStateAsync(
        SharedPackageTransferId transferId,
        ExternalIdentityRef expectedOwner,
        SharedPackageTransferState expectedState,
        SharedPackageTransferState nextState,
        DateTimeOffset changedAt,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns a bounded oldest-first batch for background reconciliation/cleanup. Implementations
    /// used in production must support this; narrow test doubles that never run cleanup may rely on
    /// the default NotSupportedException implementation.
    /// </summary>
    Task<IReadOnlyList<SharedPackageTransferRecord>> ListByStateExpiringBeforeAsync(
        SharedPackageTransferState state,
        DateTimeOffset expiresAtOrBefore,
        int limit,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException("This transfer store does not support cleanup queries.");

    /// <summary>
    /// Deletes only when owner and state still match, preventing cleanup from erasing a transfer that
    /// changed authority/state after it was selected.
    /// </summary>
    Task<bool> TryDeleteAsync(
        SharedPackageTransferId transferId,
        ExternalIdentityRef expectedOwner,
        SharedPackageTransferState expectedState,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException("This transfer store does not support cleanup deletion.");
}
