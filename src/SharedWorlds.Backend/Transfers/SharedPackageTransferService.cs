using SharedWorlds.Backend.Identity;
using SharedWorlds.Backend.Worlds;
using SharedWorlds.Core.Domain;

namespace SharedWorlds.Backend.Transfers;

public sealed record SharedPackageTransferOptions
{
    public const long FirstReleaseMaximumPackageBytes = 20L * 1024 * 1024 * 1024;
    public const int FirstReleasePartSizeBytes = 64 * 1024 * 1024;

    public SharedPackageTransferOptions(
        long maximumPackageBytes,
        int partSizeBytes,
        TimeSpan transferLifetime,
        TimeSpan authorizationLifetime)
    {
        if (maximumPackageBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumPackageBytes));
        }

        if (partSizeBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(partSizeBytes));
        }

        if (transferLifetime <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(transferLifetime));
        }

        if (authorizationLifetime <= TimeSpan.Zero || authorizationLifetime >= transferLifetime)
        {
            throw new ArgumentOutOfRangeException(nameof(authorizationLifetime));
        }

        MaximumPackageBytes = maximumPackageBytes;
        PartSizeBytes = partSizeBytes;
        TransferLifetime = transferLifetime;
        AuthorizationLifetime = authorizationLifetime;
    }

    public long MaximumPackageBytes { get; }
    public int PartSizeBytes { get; }
    public TimeSpan TransferLifetime { get; }
    public TimeSpan AuthorizationLifetime { get; }

    public static SharedPackageTransferOptions FirstReleaseDefaults { get; } = new(
        FirstReleaseMaximumPackageBytes,
        FirstReleasePartSizeBytes,
        TimeSpan.FromHours(24),
        TimeSpan.FromMinutes(15));
}

public sealed record BeginPackageUploadCommand(
    WorldId WorldId,
    RevisionId RevisionId,
    SharedPackageKind Kind,
    long ExpectedByteSize,
    string ExpectedSha256,
    RevisionId? RequiredEnvironmentRevisionId = null);

public enum BeginPackageUploadStatus
{
    Started,
    AlreadyPublished,
    NotFoundOrUnauthorized,
    InvalidRequest,
    RequiredEnvironmentMissing,
    Conflict,
    StorageIntegrityFailure
}

public sealed record BeginPackageUploadResult(
    BeginPackageUploadStatus Status,
    SharedPackageTransferRecord? Transfer);

public enum AuthorizePackagePartStatus
{
    Authorized,
    TransferNotFound,
    TransferNotActive,
    Expired,
    InvalidPart
}

public sealed record AuthorizePackagePartResult(
    AuthorizePackagePartStatus Status,
    DirectObjectTransferAuthorization? Authorization);

public sealed record SharedPackageTransferProgress(
    SharedPackageTransferRecord Transfer,
    IReadOnlyList<ImmutableUploadedPart> CompletedParts,
    bool ProviderUploadCompleted);

public enum FinalizePackageUploadStatus
{
    Finalized,
    AlreadyFinalized,
    TransferNotFound,
    TransferNotActive,
    Expired,
    IntegrityMismatch,
    PublicationConflict,
    PublicationBlocked
}

public sealed record FinalizePackageUploadResult(
    FinalizePackageUploadStatus Status,
    ImmutableStoredObject? StoredObject);

public enum AuthorizePackageDownloadStatus
{
    Authorized,
    NotFoundOrUnauthorized,
    NoHostedPackage,
    StorageIntegrityFailure
}

public sealed record AuthorizePackageDownloadResult(
    AuthorizePackageDownloadStatus Status,
    DirectObjectTransferAuthorization? Authorization,
    long? ExpectedByteSize,
    string? ExpectedSha256);

/// <summary>
/// BE-3 application service. It authorizes direct client/object-store transfer while keeping World
/// access and revision publication in Steward authority. Object storage never advances a canonical
/// World head.
/// </summary>
public sealed class SharedPackageTransferService
{
    private readonly ISharedWorldMetadataStore _worldStore;
    private readonly SharedRevisionMetadataService _revisionMetadata;
    private readonly ISharedPackageTransferStore _transferStore;
    private readonly IPrivateImmutableObjectStore _objectStore;
    private readonly SharedPackageTransferOptions _options;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly Func<SharedPackageTransferId> _newTransferId;

    public SharedPackageTransferService(
        ISharedWorldMetadataStore worldStore,
        SharedRevisionMetadataService revisionMetadata,
        ISharedPackageTransferStore transferStore,
        IPrivateImmutableObjectStore objectStore,
        Func<DateTimeOffset> utcNow,
        SharedPackageTransferOptions? options = null,
        Func<SharedPackageTransferId>? newTransferId = null)
    {
        ArgumentNullException.ThrowIfNull(worldStore);
        ArgumentNullException.ThrowIfNull(revisionMetadata);
        ArgumentNullException.ThrowIfNull(transferStore);
        ArgumentNullException.ThrowIfNull(objectStore);
        ArgumentNullException.ThrowIfNull(utcNow);
        _worldStore = worldStore;
        _revisionMetadata = revisionMetadata;
        _transferStore = transferStore;
        _objectStore = objectStore;
        _utcNow = utcNow;
        _options = options ?? SharedPackageTransferOptions.FirstReleaseDefaults;
        _newTransferId = newTransferId ?? SharedPackageTransferId.New;
    }

    public async Task<BeginPackageUploadResult> BeginUploadAsync(
        VerifiedExternalIdentity caller,
        BeginPackageUploadCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(command);

        if (!IsValidBeginCommand(command))
        {
            return new(BeginPackageUploadStatus.InvalidRequest, null);
        }

        var member = await _worldStore.LoadMemberAsync(
            command.WorldId,
            caller.Subject,
            cancellationToken);
        if (member?.Status != SharedWorldMemberStatus.Active)
        {
            return new(BeginPackageUploadStatus.NotFoundOrUnauthorized, null);
        }

        var world = await _worldStore.LoadWorldAsync(command.WorldId, cancellationToken);
        if (world is null)
        {
            return new(BeginPackageUploadStatus.NotFoundOrUnauthorized, null);
        }

        var objectKey = CreateObjectKey(command);
        var existingStatus = await CheckExistingRevisionAsync(
            caller,
            command,
            objectKey,
            cancellationToken);
        if (existingStatus is not null)
        {
            return new(existingStatus.Value, null);
        }

        if (command.Kind == SharedPackageKind.State &&
            command.RequiredEnvironmentRevisionId is { } requiredEnvironment &&
            await _revisionMetadata.GetEnvironmentRevisionMetadataAsync(
                caller,
                command.WorldId,
                requiredEnvironment,
                cancellationToken) is null)
        {
            return new(BeginPackageUploadStatus.RequiredEnvironmentMissing, null);
        }

        var existingObject = await _objectStore.InspectObjectAsync(objectKey, cancellationToken);
        if (existingObject is not null)
        {
            if (!MatchesExpectedObject(existingObject, objectKey, command.ExpectedByteSize, command.ExpectedSha256))
            {
                return new(BeginPackageUploadStatus.StorageIntegrityFailure, null);
            }

            var publication = await PublishVerifiedObjectAsync(
                caller.Subject,
                world.AdapterId,
                command,
                existingObject,
                cancellationToken);
            return new(MapExistingObjectPublication(publication), null);
        }

        var upload = await _objectStore.BeginMultipartUploadAsync(
            objectKey,
            command.ExpectedByteSize,
            NormalizeSha256(command.ExpectedSha256),
            cancellationToken);
        if (!string.Equals(upload.ObjectKey, objectKey, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Object store returned an upload for the wrong object key.");
        }

        var transferId = _newTransferId();
        if (transferId.Value == Guid.Empty)
        {
            await _objectStore.AbortMultipartUploadAsync(upload.ProviderUploadId, cancellationToken);
            throw new InvalidOperationException("Transfer ID generator returned an empty ID.");
        }

        var now = _utcNow();
        var partCount = checked((int)((command.ExpectedByteSize + _options.PartSizeBytes - 1L) / _options.PartSizeBytes));
        var transfer = new SharedPackageTransferRecord(
            transferId,
            command.WorldId,
            command.RevisionId,
            command.Kind,
            world.AdapterId,
            caller.Subject,
            objectKey,
            upload.ProviderUploadId,
            command.ExpectedByteSize,
            NormalizeSha256(command.ExpectedSha256),
            command.RequiredEnvironmentRevisionId,
            _options.PartSizeBytes,
            partCount,
            now,
            now + _options.TransferLifetime,
            SharedPackageTransferState.Active);

        if (!await _transferStore.TryCreateAsync(transfer, cancellationToken))
        {
            await _objectStore.AbortMultipartUploadAsync(upload.ProviderUploadId, cancellationToken);
            throw new InvalidOperationException("Generated transfer ID already exists.");
        }

        return new(BeginPackageUploadStatus.Started, transfer);
    }

    public async Task<SharedPackageTransferProgress?> GetProgressAsync(
        VerifiedExternalIdentity caller,
        SharedPackageTransferId transferId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ValidateTransferId(transferId);
        var transfer = await LoadOwnedTransferAsync(caller.Subject, transferId, cancellationToken);
        if (transfer is null)
        {
            return null;
        }

        if (transfer.State != SharedPackageTransferState.Active)
        {
            return new(transfer, Array.Empty<ImmutableUploadedPart>(), transfer.State == SharedPackageTransferState.Finalized);
        }

        var provider = await _objectStore.GetMultipartUploadAsync(
            transfer.ProviderUploadId,
            cancellationToken);
        if (provider is null || !string.Equals(provider.ObjectKey, transfer.ObjectKey, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Durable transfer references a missing or mismatched provider upload.");
        }

        return new(transfer, provider.CompletedParts, provider.IsCompleted);
    }

    public async Task<AuthorizePackagePartResult> AuthorizePartAsync(
        VerifiedExternalIdentity caller,
        SharedPackageTransferId transferId,
        int partNumber,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ValidateTransferId(transferId);
        var transfer = await LoadOwnedTransferAsync(caller.Subject, transferId, cancellationToken);
        if (transfer is null)
        {
            return new(AuthorizePackagePartStatus.TransferNotFound, null);
        }

        if (transfer.State != SharedPackageTransferState.Active)
        {
            return new(AuthorizePackagePartStatus.TransferNotActive, null);
        }

        var now = _utcNow();
        if (transfer.ExpiresAt <= now)
        {
            return new(AuthorizePackagePartStatus.Expired, null);
        }

        if (partNumber <= 0 || partNumber > transfer.PartCount)
        {
            return new(AuthorizePackagePartStatus.InvalidPart, null);
        }

        var partStart = (long)(partNumber - 1) * transfer.PartSizeBytes;
        var expectedPartBytes = Math.Min(
            transfer.PartSizeBytes,
            transfer.ExpectedByteSize - partStart);
        var authorization = await _objectStore.AuthorizeUploadPartAsync(
            transfer.ProviderUploadId,
            partNumber,
            expectedPartBytes,
            Min(transfer.ExpiresAt, now + _options.AuthorizationLifetime),
            cancellationToken);

        return new(AuthorizePackagePartStatus.Authorized, authorization);
    }

    public async Task<FinalizePackageUploadResult> FinalizeAsync(
        VerifiedExternalIdentity caller,
        SharedPackageTransferId transferId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ValidateTransferId(transferId);
        var transfer = await LoadOwnedTransferAsync(caller.Subject, transferId, cancellationToken);
        if (transfer is null)
        {
            return new(FinalizePackageUploadStatus.TransferNotFound, null);
        }

        if (transfer.State == SharedPackageTransferState.Finalized)
        {
            var stored = await _objectStore.InspectObjectAsync(transfer.ObjectKey, cancellationToken);
            return new(FinalizePackageUploadStatus.AlreadyFinalized, stored);
        }

        if (transfer.State == SharedPackageTransferState.IntegrityFailed)
        {
            return new(FinalizePackageUploadStatus.IntegrityMismatch, null);
        }

        if (transfer.State == SharedPackageTransferState.PublicationConflict)
        {
            return new(FinalizePackageUploadStatus.PublicationConflict, null);
        }

        if (transfer.State != SharedPackageTransferState.Active)
        {
            return new(FinalizePackageUploadStatus.TransferNotActive, null);
        }

        var now = _utcNow();
        if (transfer.ExpiresAt <= now)
        {
            return new(FinalizePackageUploadStatus.Expired, null);
        }

        var storedObject = await _objectStore.CompleteMultipartUploadAsync(
            transfer.ProviderUploadId,
            cancellationToken);
        if (!MatchesExpectedObject(
                storedObject,
                transfer.ObjectKey,
                transfer.ExpectedByteSize,
                transfer.ExpectedSha256))
        {
            await _transferStore.TrySetStateAsync(
                transfer.Id,
                transfer.Owner,
                SharedPackageTransferState.Active,
                SharedPackageTransferState.IntegrityFailed,
                now,
                cancellationToken);
            return new(FinalizePackageUploadStatus.IntegrityMismatch, storedObject);
        }

        var command = new BeginPackageUploadCommand(
            transfer.WorldId,
            transfer.RevisionId,
            transfer.Kind,
            transfer.ExpectedByteSize,
            transfer.ExpectedSha256,
            transfer.RequiredEnvironmentRevisionId);
        var publication = await PublishVerifiedObjectAsync(
            transfer.Owner,
            transfer.AdapterId,
            command,
            storedObject,
            cancellationToken);

        if (publication == RecordRevisionMetadataStatus.Conflict)
        {
            await _transferStore.TrySetStateAsync(
                transfer.Id,
                transfer.Owner,
                SharedPackageTransferState.Active,
                SharedPackageTransferState.PublicationConflict,
                now,
                cancellationToken);
            return new(FinalizePackageUploadStatus.PublicationConflict, storedObject);
        }

        if (publication is RecordRevisionMetadataStatus.WorldNotFound or
            RecordRevisionMetadataStatus.AdapterMismatch or
            RecordRevisionMetadataStatus.RequiredEnvironmentMissing)
        {
            return new(FinalizePackageUploadStatus.PublicationBlocked, storedObject);
        }

        var finalized = await _transferStore.TrySetStateAsync(
            transfer.Id,
            transfer.Owner,
            SharedPackageTransferState.Active,
            SharedPackageTransferState.Finalized,
            now,
            cancellationToken);
        if (!finalized)
        {
            var current = await _transferStore.LoadAsync(transfer.Id, cancellationToken);
            return current?.State == SharedPackageTransferState.Finalized
                ? new(FinalizePackageUploadStatus.AlreadyFinalized, storedObject)
                : new(FinalizePackageUploadStatus.TransferNotActive, storedObject);
        }

        return new(FinalizePackageUploadStatus.Finalized, storedObject);
    }

    public async Task<AuthorizePackageDownloadResult> AuthorizeDownloadAsync(
        VerifiedExternalIdentity caller,
        WorldId worldId,
        RevisionId revisionId,
        SharedPackageKind kind,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(caller);
        if (worldId.Value == Guid.Empty || revisionId.Value == Guid.Empty)
        {
            return new(AuthorizePackageDownloadStatus.NotFoundOrUnauthorized, null, null, null);
        }

        string objectKey;
        long expectedBytes;
        string expectedSha;

        if (kind == SharedPackageKind.State)
        {
            var state = await _revisionMetadata.GetStateRevisionMetadataAsync(
                caller,
                worldId,
                revisionId,
                cancellationToken);
            if (state is null)
            {
                return new(AuthorizePackageDownloadStatus.NotFoundOrUnauthorized, null, null, null);
            }

            objectKey = state.PackageObjectKey;
            expectedBytes = state.ByteSize;
            expectedSha = state.Sha256;
        }
        else
        {
            var environment = await _revisionMetadata.GetEnvironmentRevisionMetadataAsync(
                caller,
                worldId,
                revisionId,
                cancellationToken);
            if (environment is null)
            {
                return new(AuthorizePackageDownloadStatus.NotFoundOrUnauthorized, null, null, null);
            }

            if (environment.ByteSize is not { } byteSize || environment.Sha256 is not { } sha256)
            {
                return new(AuthorizePackageDownloadStatus.NoHostedPackage, null, null, null);
            }

            objectKey = environment.ArtifactReference;
            expectedBytes = byteSize;
            expectedSha = sha256;
        }

        var stored = await _objectStore.InspectObjectAsync(objectKey, cancellationToken);
        if (stored is null || !MatchesExpectedObject(stored, objectKey, expectedBytes, expectedSha))
        {
            return new(AuthorizePackageDownloadStatus.StorageIntegrityFailure, null, expectedBytes, expectedSha);
        }

        var authorization = await _objectStore.AuthorizeDownloadAsync(
            objectKey,
            expectedBytes,
            _utcNow() + _options.AuthorizationLifetime,
            cancellationToken);
        return new(
            AuthorizePackageDownloadStatus.Authorized,
            authorization,
            expectedBytes,
            NormalizeSha256(expectedSha));
    }

    private async Task<BeginPackageUploadStatus?> CheckExistingRevisionAsync(
        VerifiedExternalIdentity caller,
        BeginPackageUploadCommand command,
        string expectedObjectKey,
        CancellationToken cancellationToken)
    {
        if (command.Kind == SharedPackageKind.State)
        {
            var existing = await _revisionMetadata.GetStateRevisionMetadataAsync(
                caller,
                command.WorldId,
                command.RevisionId,
                cancellationToken);
            if (existing is null)
            {
                return null;
            }

            return existing.ByteSize == command.ExpectedByteSize &&
                   string.Equals(existing.Sha256, command.ExpectedSha256, StringComparison.OrdinalIgnoreCase) &&
                   existing.RequiredEnvironmentRevisionId == command.RequiredEnvironmentRevisionId &&
                   string.Equals(existing.PackageObjectKey, expectedObjectKey, StringComparison.Ordinal)
                ? BeginPackageUploadStatus.AlreadyPublished
                : BeginPackageUploadStatus.Conflict;
        }

        var environment = await _revisionMetadata.GetEnvironmentRevisionMetadataAsync(
            caller,
            command.WorldId,
            command.RevisionId,
            cancellationToken);
        if (environment is null)
        {
            return null;
        }

        return environment.ByteSize == command.ExpectedByteSize &&
               string.Equals(environment.Sha256, command.ExpectedSha256, StringComparison.OrdinalIgnoreCase) &&
               string.Equals(environment.ArtifactReference, expectedObjectKey, StringComparison.Ordinal)
            ? BeginPackageUploadStatus.AlreadyPublished
            : BeginPackageUploadStatus.Conflict;
    }

    private async Task<RecordRevisionMetadataStatus> PublishVerifiedObjectAsync(
        ExternalIdentityRef publisher,
        string adapterId,
        BeginPackageUploadCommand command,
        ImmutableStoredObject storedObject,
        CancellationToken cancellationToken)
    {
        if (command.Kind == SharedPackageKind.State)
        {
            return await _revisionMetadata.RecordVerifiedStateRevisionAsync(
                new SharedStateRevisionMetadata(
                    command.WorldId,
                    command.RevisionId,
                    adapterId,
                    storedObject.ObjectKey,
                    storedObject.ByteSize,
                    NormalizeSha256(storedObject.Sha256),
                    command.RequiredEnvironmentRevisionId,
                    publisher,
                    _utcNow()),
                cancellationToken);
        }

        return await _revisionMetadata.RecordVerifiedEnvironmentRevisionAsync(
            new SharedEnvironmentRevisionMetadata(
                command.WorldId,
                command.RevisionId,
                adapterId,
                storedObject.ObjectKey,
                storedObject.ByteSize,
                NormalizeSha256(storedObject.Sha256),
                publisher,
                _utcNow()),
            cancellationToken);
    }

    private async Task<SharedPackageTransferRecord?> LoadOwnedTransferAsync(
        ExternalIdentityRef caller,
        SharedPackageTransferId transferId,
        CancellationToken cancellationToken)
    {
        var transfer = await _transferStore.LoadAsync(transferId, cancellationToken);
        return transfer?.Owner == caller ? transfer : null;
    }

    private bool IsValidBeginCommand(BeginPackageUploadCommand command)
    {
        if (command.WorldId.Value == Guid.Empty ||
            command.RevisionId.Value == Guid.Empty ||
            command.ExpectedByteSize <= 0 ||
            command.ExpectedByteSize > _options.MaximumPackageBytes ||
            !IsValidSha256(command.ExpectedSha256))
        {
            return false;
        }

        return command.Kind switch
        {
            SharedPackageKind.State => true,
            SharedPackageKind.Environment => command.RequiredEnvironmentRevisionId is null,
            _ => false
        };
    }

    private static string CreateObjectKey(BeginPackageUploadCommand command)
    {
        var kind = command.Kind == SharedPackageKind.State ? "state" : "environment";
        return $"packages/{command.WorldId}/{kind}/{command.RevisionId}/{NormalizeSha256(command.ExpectedSha256).ToLowerInvariant()}.package";
    }

    private static bool MatchesExpectedObject(
        ImmutableStoredObject stored,
        string expectedObjectKey,
        long expectedByteSize,
        string expectedSha256)
        => string.Equals(stored.ObjectKey, expectedObjectKey, StringComparison.Ordinal) &&
           stored.ByteSize == expectedByteSize &&
           string.Equals(stored.Sha256, expectedSha256, StringComparison.OrdinalIgnoreCase);

    private static BeginPackageUploadStatus MapExistingObjectPublication(
        RecordRevisionMetadataStatus publication)
        => publication switch
        {
            RecordRevisionMetadataStatus.Recorded or RecordRevisionMetadataStatus.AlreadyRecorded =>
                BeginPackageUploadStatus.AlreadyPublished,
            RecordRevisionMetadataStatus.RequiredEnvironmentMissing =>
                BeginPackageUploadStatus.RequiredEnvironmentMissing,
            RecordRevisionMetadataStatus.Conflict => BeginPackageUploadStatus.Conflict,
            RecordRevisionMetadataStatus.WorldNotFound or RecordRevisionMetadataStatus.AdapterMismatch =>
                BeginPackageUploadStatus.NotFoundOrUnauthorized,
            _ => throw new InvalidOperationException("Unexpected revision publication result.")
        };

    private static bool IsValidSha256(string value)
        => !string.IsNullOrWhiteSpace(value) &&
           value.Length == 64 &&
           value.All(Uri.IsHexDigit);

    private static string NormalizeSha256(string value) => value.ToUpperInvariant();

    private static DateTimeOffset Min(DateTimeOffset left, DateTimeOffset right)
        => left <= right ? left : right;

    private static void ValidateTransferId(SharedPackageTransferId transferId)
    {
        if (transferId.Value == Guid.Empty)
        {
            throw new ArgumentException("Transfer ID is required.", nameof(transferId));
        }
    }
}
