using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SharedWorlds.Backend.Identity;
using SharedWorlds.Core.Domain;
using SharedWorlds.Core.Environment;
using SharedWorlds.Core.Worlds;

namespace SharedWorlds.Backend.Transfers;

public readonly record struct PrivateSnapshotTransferId(Guid Value)
{
    public static PrivateSnapshotTransferId New() => new(Guid.NewGuid());
    public override string ToString() => Value.ToString("N");
}

public enum PrivateSnapshotTransferState
{
    Provisioning,
    Active,
    IntegrityFailed,
    PublicationBlocked,
    SnapshotConflict,
    Finalized
}

public sealed record PrivateSnapshotTransferOptions
{
    public PrivateSnapshotTransferOptions(
        long maximumPackageBytes,
        int partSizeBytes,
        TimeSpan transferLifetime,
        TimeSpan authorizationLifetime)
    {
        if (maximumPackageBytes <= 0 ||
            maximumPackageBytes > OwnedWorldSnapshot.MaximumStatePackageByteSize)
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

    public static PrivateSnapshotTransferOptions FirstReleaseDefaults { get; } = new(
        OwnedWorldSnapshot.MaximumStatePackageByteSize,
        SharedPackageTransferOptions.FirstReleasePartSizeBytes,
        TimeSpan.FromHours(24),
        TimeSpan.FromMinutes(15));
}

public sealed record BeginPrivateSnapshotUploadCommand(
    WorldId WorldId,
    RevisionId StateRevisionId,
    RevisionId EnvironmentRevisionId,
    string GameAdapterId,
    long ExpectedByteSize,
    string ExpectedSha256,
    EnvironmentManifest EnvironmentManifest);

public sealed record PrivateSnapshotTransferRecord(
    PrivateSnapshotTransferId Id,
    string OwnerProvider,
    string OwnerExternalId,
    string SourceInstallationId,
    WorldId WorldId,
    RevisionId StateRevisionId,
    RevisionId EnvironmentRevisionId,
    string GameAdapterId,
    string ObjectKey,
    string ProviderUploadId,
    long ExpectedByteSize,
    string ExpectedSha256,
    EnvironmentManifest EnvironmentManifest,
    int PartSizeBytes,
    int PartCount,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt,
    PrivateSnapshotTransferState State,
    DateTimeOffset? StateChangedAt = null);

/// <summary>
/// Durable owner-private upload intent. Transfer IDs are scoped to the authenticated owner and source
/// installation. Implementations must serialize creation by deterministic object key and state changes
/// by transfer ID.
/// </summary>
public interface IPrivateSnapshotTransferStore
{
    Task<bool> TryCreateAsync(
        PrivateSnapshotTransferRecord transfer,
        CancellationToken cancellationToken = default);

    Task<PrivateSnapshotTransferRecord?> LoadAsync(
        PrivateSnapshotTransferId transferId,
        CancellationToken cancellationToken = default);

    Task<PrivateSnapshotTransferRecord?> LoadInFlightByObjectKeyAsync(
        string objectKey,
        CancellationToken cancellationToken = default);

    Task<bool> TryActivateProvisioningAsync(
        PrivateSnapshotTransferId transferId,
        string ownerProvider,
        string ownerExternalId,
        string sourceInstallationId,
        string expectedPlaceholderProviderUploadId,
        string providerUploadId,
        CancellationToken cancellationToken = default);

    Task<bool> TrySetStateAsync(
        PrivateSnapshotTransferId transferId,
        string ownerProvider,
        string ownerExternalId,
        string sourceInstallationId,
        PrivateSnapshotTransferState expectedState,
        PrivateSnapshotTransferState nextState,
        DateTimeOffset changedAt,
        CancellationToken cancellationToken = default);
}

public enum BeginPrivateSnapshotUploadStatus
{
    Started,
    AlreadyPublished,
    NotFoundOrUnauthorized,
    InvalidRequest,
    Conflict,
    StorageIntegrityFailure
}

public sealed record BeginPrivateSnapshotUploadResult(
    BeginPrivateSnapshotUploadStatus Status,
    PrivateSnapshotTransferRecord? Transfer);

public enum AuthorizePrivateSnapshotPartStatus
{
    Authorized,
    TransferNotFound,
    TransferNotActive,
    Expired,
    InvalidPart
}

public sealed record AuthorizePrivateSnapshotPartResult(
    AuthorizePrivateSnapshotPartStatus Status,
    DirectObjectTransferAuthorization? Authorization);

public sealed record PrivateSnapshotTransferProgress(
    PrivateSnapshotTransferRecord Transfer,
    IReadOnlyList<ImmutableUploadedPart> CompletedParts,
    bool ProviderUploadCompleted);

public enum FinalizePrivateSnapshotUploadStatus
{
    Finalized,
    AlreadyFinalized,
    TransferNotFound,
    TransferNotActive,
    Expired,
    IntegrityMismatch,
    PublicationBlocked,
    SnapshotConflict
}

public sealed record FinalizePrivateSnapshotUploadResult(
    FinalizePrivateSnapshotUploadStatus Status,
    OwnedWorldSnapshot? Snapshot);

public enum AuthorizePrivateSnapshotDownloadStatus
{
    Authorized,
    NotFoundOrUnauthorized,
    Unavailable,
    AlreadyHere,
    Conflict,
    StorageIntegrityFailure
}

/// <summary>
/// Target-visible download plan. The backend object key is deliberately absent.
/// </summary>
public sealed record PrivateSnapshotDownloadPlan(
    WorldId WorldId,
    string SourceInstallationId,
    RevisionId StateRevisionId,
    RevisionId EnvironmentRevisionId,
    string GameAdapterId,
    long ExpectedByteSize,
    string ExpectedSha256,
    EnvironmentManifest EnvironmentManifest,
    DirectObjectTransferAuthorization Authorization);

public sealed record AuthorizePrivateSnapshotDownloadResult(
    AuthorizePrivateSnapshotDownloadStatus Status,
    PrivateSnapshotDownloadPlan? Plan,
    string Reason);

/// <summary>
/// Resumable direct-object transfer authority for owner-private snapshots. Upload sessions belong only
/// to the authenticated owner and exact source installation. Download authorization is issued only for
/// the exact snapshot selected by Bring Here head+byte authority for the authenticated target install.
/// Object storage never chooses a World head.
/// </summary>
public sealed class PrivateSnapshotTransferService
{
    private static readonly JsonSerializerOptions ManifestJsonOptions = new(JsonSerializerDefaults.Web);
    private readonly IOwnedWorldLocationStore _locations;
    private readonly IOwnedWorldSnapshotStore _snapshots;
    private readonly OwnedWorldSnapshotRegistry _snapshotRegistry;
    private readonly BringHereSnapshotAuthorityService _bringHereAuthority = new();
    private readonly IPrivateSnapshotTransferStore _transfers;
    private readonly IPrivateImmutableObjectStore _objectStore;
    private readonly PrivateSnapshotTransferOptions _options;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly Func<PrivateSnapshotTransferId> _newTransferId;

    public PrivateSnapshotTransferService(
        IOwnedWorldLocationStore locations,
        IOwnedWorldSnapshotStore snapshots,
        IPrivateSnapshotTransferStore transfers,
        IPrivateImmutableObjectStore objectStore,
        Func<DateTimeOffset> utcNow,
        PrivateSnapshotTransferOptions? options = null,
        Func<PrivateSnapshotTransferId>? newTransferId = null)
    {
        ArgumentNullException.ThrowIfNull(locations);
        ArgumentNullException.ThrowIfNull(snapshots);
        ArgumentNullException.ThrowIfNull(transfers);
        ArgumentNullException.ThrowIfNull(objectStore);
        ArgumentNullException.ThrowIfNull(utcNow);
        _locations = locations;
        _snapshots = snapshots;
        _snapshotRegistry = new OwnedWorldSnapshotRegistry(locations, snapshots);
        _transfers = transfers;
        _objectStore = objectStore;
        _utcNow = utcNow;
        _options = options ?? PrivateSnapshotTransferOptions.FirstReleaseDefaults;
        _newTransferId = newTransferId ?? PrivateSnapshotTransferId.New;
    }

    public async Task<BeginPrivateSnapshotUploadResult> BeginUploadAsync(
        StewardAuthenticatedCaller caller,
        BeginPrivateSnapshotUploadCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(command);
        if (!IsValidBeginCommand(command))
        {
            return new(BeginPrivateSnapshotUploadStatus.InvalidRequest, null);
        }

        var owner = ToUserIdentity(caller.Identity);
        if (!await IsCurrentOwnedSourceAsync(
                owner,
                caller.InstallationId,
                command,
                cancellationToken))
        {
            return new(BeginPrivateSnapshotUploadStatus.NotFoundOrUnauthorized, null);
        }

        var objectKey = CreateObjectKey(owner, caller.InstallationId, command);
        var existingSnapshot = await _snapshots.LoadExactAsync(
            owner.Provider,
            owner.ExternalId,
            command.WorldId,
            caller.InstallationId,
            command.StateRevisionId,
            command.EnvironmentRevisionId,
            cancellationToken);
        if (existingSnapshot is not null)
        {
            return MatchesPublishedSnapshot(existingSnapshot, objectKey, command)
                ? new(BeginPrivateSnapshotUploadStatus.AlreadyPublished, null)
                : new(BeginPrivateSnapshotUploadStatus.Conflict, null);
        }

        var existingObject = await _objectStore.InspectObjectAsync(objectKey, cancellationToken);
        if (existingObject is not null)
        {
            if (!MatchesExpectedObject(
                    existingObject,
                    objectKey,
                    command.ExpectedByteSize,
                    command.ExpectedSha256))
            {
                return new(BeginPrivateSnapshotUploadStatus.StorageIntegrityFailure, null);
            }

            var publication = await PublishSnapshotAsync(
                owner,
                caller.InstallationId,
                command,
                existingObject,
                cancellationToken);
            return publication.Result switch
            {
                OwnedWorldSnapshotWriteResult.Created or OwnedWorldSnapshotWriteResult.NoChange =>
                    new(BeginPrivateSnapshotUploadStatus.AlreadyPublished, null),
                OwnedWorldSnapshotWriteResult.Conflict =>
                    new(BeginPrivateSnapshotUploadStatus.Conflict, null),
                _ => throw new InvalidOperationException("Unexpected private snapshot publication result.")
            };
        }

        var transferId = _newTransferId();
        if (transferId.Value == Guid.Empty)
        {
            throw new InvalidOperationException("Private snapshot transfer ID generator returned an empty ID.");
        }

        var now = _utcNow();
        var partCount = checked((int)(
            (command.ExpectedByteSize + _options.PartSizeBytes - 1L) /
            _options.PartSizeBytes));
        var placeholder = CreateProvisioningPlaceholder(transferId);
        var provisioning = new PrivateSnapshotTransferRecord(
            transferId,
            owner.Provider,
            owner.ExternalId,
            caller.InstallationId,
            command.WorldId,
            command.StateRevisionId,
            command.EnvironmentRevisionId,
            command.GameAdapterId,
            objectKey,
            placeholder,
            command.ExpectedByteSize,
            NormalizeSha256(command.ExpectedSha256),
            command.EnvironmentManifest,
            _options.PartSizeBytes,
            partCount,
            now,
            now + _options.TransferLifetime,
            PrivateSnapshotTransferState.Provisioning);

        if (!await _transfers.TryCreateAsync(provisioning, cancellationToken))
        {
            var existing = await _transfers.LoadInFlightByObjectKeyAsync(
                objectKey,
                cancellationToken)
                ?? throw new InvalidOperationException(
                    "Private transfer intent could not be created and no in-flight intent exists.");
            if (!MatchesTransferIntent(existing, provisioning))
            {
                return new(BeginPrivateSnapshotUploadStatus.Conflict, null);
            }

            if (existing.State == PrivateSnapshotTransferState.Active)
            {
                return new(BeginPrivateSnapshotUploadStatus.Started, existing);
            }

            if (existing.State != PrivateSnapshotTransferState.Provisioning)
            {
                return new(BeginPrivateSnapshotUploadStatus.Conflict, null);
            }

            provisioning = existing;
            placeholder = existing.ProviderUploadId;
        }

        var upload = await _objectStore.BeginMultipartUploadAsync(
            provisioning.ObjectKey,
            provisioning.ExpectedByteSize,
            provisioning.ExpectedSha256,
            cancellationToken);
        if (!string.Equals(upload.ObjectKey, provisioning.ObjectKey, StringComparison.Ordinal))
        {
            await _objectStore.AbortMultipartUploadAsync(upload.ProviderUploadId, cancellationToken);
            throw new InvalidOperationException(
                "Object storage returned a private upload for the wrong object key.");
        }

        if (await _transfers.TryActivateProvisioningAsync(
                provisioning.Id,
                provisioning.OwnerProvider,
                provisioning.OwnerExternalId,
                provisioning.SourceInstallationId,
                placeholder,
                upload.ProviderUploadId,
                cancellationToken))
        {
            return new(
                BeginPrivateSnapshotUploadStatus.Started,
                provisioning with
                {
                    ProviderUploadId = upload.ProviderUploadId,
                    State = PrivateSnapshotTransferState.Active
                });
        }

        var current = await _transfers.LoadAsync(provisioning.Id, cancellationToken);
        if (current is not null &&
            current.State == PrivateSnapshotTransferState.Active &&
            MatchesTransferIntent(current, provisioning))
        {
            if (!string.Equals(
                    current.ProviderUploadId,
                    upload.ProviderUploadId,
                    StringComparison.Ordinal))
            {
                await _objectStore.AbortMultipartUploadAsync(
                    upload.ProviderUploadId,
                    cancellationToken);
            }

            return new(BeginPrivateSnapshotUploadStatus.Started, current);
        }

        await _objectStore.AbortMultipartUploadAsync(upload.ProviderUploadId, cancellationToken);
        throw new InvalidOperationException(
            "Private transfer provisioning changed unexpectedly while attaching provider upload state.");
    }

    public async Task<PrivateSnapshotTransferProgress?> GetProgressAsync(
        StewardAuthenticatedCaller caller,
        PrivateSnapshotTransferId transferId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ValidateTransferId(transferId);
        var transfer = await LoadOwnedTransferAsync(caller, transferId, cancellationToken);
        if (transfer is null)
        {
            return null;
        }

        if (transfer.State != PrivateSnapshotTransferState.Active)
        {
            return new(
                transfer,
                Array.Empty<ImmutableUploadedPart>(),
                transfer.State == PrivateSnapshotTransferState.Finalized);
        }

        var provider = await _objectStore.GetMultipartUploadAsync(
            transfer.ProviderUploadId,
            cancellationToken);
        if (provider is null ||
            !string.Equals(provider.ObjectKey, transfer.ObjectKey, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Durable private transfer references missing or mismatched provider upload state.");
        }

        return new(transfer, provider.CompletedParts, provider.IsCompleted);
    }

    public async Task<AuthorizePrivateSnapshotPartResult> AuthorizePartAsync(
        StewardAuthenticatedCaller caller,
        PrivateSnapshotTransferId transferId,
        int partNumber,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ValidateTransferId(transferId);
        var transfer = await LoadOwnedTransferAsync(caller, transferId, cancellationToken);
        if (transfer is null)
        {
            return new(AuthorizePrivateSnapshotPartStatus.TransferNotFound, null);
        }

        if (transfer.State != PrivateSnapshotTransferState.Active)
        {
            return new(AuthorizePrivateSnapshotPartStatus.TransferNotActive, null);
        }

        var now = _utcNow();
        if (transfer.ExpiresAt <= now)
        {
            return new(AuthorizePrivateSnapshotPartStatus.Expired, null);
        }

        if (partNumber <= 0 || partNumber > transfer.PartCount)
        {
            return new(AuthorizePrivateSnapshotPartStatus.InvalidPart, null);
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
        return new(AuthorizePrivateSnapshotPartStatus.Authorized, authorization);
    }

    public async Task<FinalizePrivateSnapshotUploadResult> FinalizeAsync(
        StewardAuthenticatedCaller caller,
        PrivateSnapshotTransferId transferId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ValidateTransferId(transferId);
        var transfer = await LoadOwnedTransferAsync(caller, transferId, cancellationToken);
        if (transfer is null)
        {
            return new(FinalizePrivateSnapshotUploadStatus.TransferNotFound, null);
        }

        if (transfer.State == PrivateSnapshotTransferState.Finalized)
        {
            var snapshot = await _snapshots.LoadExactAsync(
                transfer.OwnerProvider,
                transfer.OwnerExternalId,
                transfer.WorldId,
                transfer.SourceInstallationId,
                transfer.StateRevisionId,
                transfer.EnvironmentRevisionId,
                cancellationToken);
            return new(FinalizePrivateSnapshotUploadStatus.AlreadyFinalized, snapshot);
        }

        if (transfer.State == PrivateSnapshotTransferState.IntegrityFailed)
        {
            return new(FinalizePrivateSnapshotUploadStatus.IntegrityMismatch, null);
        }

        if (transfer.State == PrivateSnapshotTransferState.PublicationBlocked)
        {
            return new(FinalizePrivateSnapshotUploadStatus.PublicationBlocked, null);
        }

        if (transfer.State == PrivateSnapshotTransferState.SnapshotConflict)
        {
            return new(FinalizePrivateSnapshotUploadStatus.SnapshotConflict, null);
        }

        if (transfer.State != PrivateSnapshotTransferState.Active)
        {
            return new(FinalizePrivateSnapshotUploadStatus.TransferNotActive, null);
        }

        var now = _utcNow();
        if (transfer.ExpiresAt <= now)
        {
            return new(FinalizePrivateSnapshotUploadStatus.Expired, null);
        }

        var stored = await _objectStore.CompleteMultipartUploadAsync(
            transfer.ProviderUploadId,
            cancellationToken);
        if (!MatchesExpectedObject(
                stored,
                transfer.ObjectKey,
                transfer.ExpectedByteSize,
                transfer.ExpectedSha256))
        {
            await SetTerminalStateAsync(
                transfer,
                PrivateSnapshotTransferState.IntegrityFailed,
                now,
                cancellationToken);
            return new(FinalizePrivateSnapshotUploadStatus.IntegrityMismatch, null);
        }

        OwnedWorldSnapshotWriteDecision publication;
        try
        {
            publication = await PublishSnapshotAsync(
                ToUserIdentity(caller.Identity),
                caller.InstallationId,
                new BeginPrivateSnapshotUploadCommand(
                    transfer.WorldId,
                    transfer.StateRevisionId,
                    transfer.EnvironmentRevisionId,
                    transfer.GameAdapterId,
                    transfer.ExpectedByteSize,
                    transfer.ExpectedSha256,
                    transfer.EnvironmentManifest),
                stored,
                cancellationToken);
        }
        catch (InvalidOperationException)
        {
            await SetTerminalStateAsync(
                transfer,
                PrivateSnapshotTransferState.PublicationBlocked,
                now,
                cancellationToken);
            return new(FinalizePrivateSnapshotUploadStatus.PublicationBlocked, null);
        }

        if (publication.Result == OwnedWorldSnapshotWriteResult.Conflict)
        {
            await SetTerminalStateAsync(
                transfer,
                PrivateSnapshotTransferState.SnapshotConflict,
                now,
                cancellationToken);
            return new(FinalizePrivateSnapshotUploadStatus.SnapshotConflict, publication.Current);
        }

        var finalized = await _transfers.TrySetStateAsync(
            transfer.Id,
            transfer.OwnerProvider,
            transfer.OwnerExternalId,
            transfer.SourceInstallationId,
            PrivateSnapshotTransferState.Active,
            PrivateSnapshotTransferState.Finalized,
            now,
            cancellationToken);
        if (!finalized)
        {
            var current = await _transfers.LoadAsync(transfer.Id, cancellationToken);
            return current?.State == PrivateSnapshotTransferState.Finalized
                ? new(FinalizePrivateSnapshotUploadStatus.AlreadyFinalized, publication.Current)
                : new(FinalizePrivateSnapshotUploadStatus.TransferNotActive, publication.Current);
        }

        return new(FinalizePrivateSnapshotUploadStatus.Finalized, publication.Current);
    }

    public async Task<AuthorizePrivateSnapshotDownloadResult> AuthorizeDownloadAsync(
        StewardAuthenticatedCaller caller,
        WorldId worldId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(caller);
        if (worldId.Value == Guid.Empty)
        {
            return new(
                AuthorizePrivateSnapshotDownloadStatus.NotFoundOrUnauthorized,
                null,
                "The private World was not found or is not authorized.");
        }

        var owner = ToUserIdentity(caller.Identity);
        var installation = await _locations.GetInstallationAsync(
            caller.InstallationId,
            cancellationToken);
        if (installation is null || !SameOwner(installation, owner))
        {
            return new(
                AuthorizePrivateSnapshotDownloadStatus.NotFoundOrUnauthorized,
                null,
                "The private World was not found or is not authorized.");
        }

        var claims = await _locations.ListWorldLocationsAsync(
            worldId,
            owner.Provider,
            owner.ExternalId,
            cancellationToken);
        var snapshots = await _snapshots.ListWorldSnapshotsAsync(
            owner.Provider,
            owner.ExternalId,
            worldId,
            BringHereSnapshotAuthorityService.MaximumSnapshotsPerWorld + 1,
            cancellationToken);
        var decision = _bringHereAuthority.Resolve(
            worldId,
            owner,
            caller.InstallationId,
            claims,
            snapshots);
        if (decision.Availability != BringHereAvailability.Available ||
            decision.Source is null ||
            decision.Snapshot is null)
        {
            return new(
                MapDownloadStatus(decision.Availability),
                null,
                decision.Reason);
        }

        var snapshot = decision.Snapshot;
        var stored = await _objectStore.InspectObjectAsync(
            snapshot.StatePackageObjectKey,
            cancellationToken);
        if (stored is null ||
            !MatchesExpectedObject(
                stored,
                snapshot.StatePackageObjectKey,
                snapshot.StatePackageByteSize,
                snapshot.StatePackageSha256))
        {
            return new(
                AuthorizePrivateSnapshotDownloadStatus.StorageIntegrityFailure,
                null,
                "The selected private snapshot object is missing or no longer matches its verified immutable identity.");
        }

        var authorization = await _objectStore.AuthorizeDownloadAsync(
            snapshot.StatePackageObjectKey,
            snapshot.StatePackageByteSize,
            _utcNow() + _options.AuthorizationLifetime,
            cancellationToken);
        return new(
            AuthorizePrivateSnapshotDownloadStatus.Authorized,
            new PrivateSnapshotDownloadPlan(
                snapshot.WorldId,
                snapshot.InstallationId,
                snapshot.StateRevisionId,
                snapshot.EnvironmentRevisionId,
                snapshot.GameAdapterId,
                snapshot.StatePackageByteSize,
                snapshot.StatePackageSha256,
                snapshot.EnvironmentManifest,
                authorization),
            decision.Reason);
    }

    private async Task<bool> IsCurrentOwnedSourceAsync(
        UserIdentity owner,
        string installationId,
        BeginPrivateSnapshotUploadCommand command,
        CancellationToken cancellationToken)
    {
        var installation = await _locations.GetInstallationAsync(
            installationId,
            cancellationToken);
        if (installation is null || !SameOwner(installation, owner))
        {
            return false;
        }

        var claims = await _locations.ListWorldLocationsAsync(
            command.WorldId,
            owner.Provider,
            owner.ExternalId,
            cancellationToken);
        var sourceClaims = claims
            .Where(claim => string.Equals(
                claim.InstallationId,
                installationId,
                StringComparison.Ordinal))
            .ToArray();
        if (sourceClaims.Length != 1)
        {
            return false;
        }

        var source = sourceClaims[0];
        return source.StateRevisionId == command.StateRevisionId &&
               source.EnvironmentRevisionId == command.EnvironmentRevisionId &&
               (source.Presentation is null || string.Equals(
                   source.Presentation.GameAdapterId,
                   command.GameAdapterId,
                   StringComparison.Ordinal));
    }

    private async Task<OwnedWorldSnapshotWriteDecision> PublishSnapshotAsync(
        UserIdentity owner,
        string installationId,
        BeginPrivateSnapshotUploadCommand command,
        ImmutableStoredObject stored,
        CancellationToken cancellationToken)
        => await _snapshotRegistry.PublishAsync(
            owner,
            installationId,
            command.WorldId,
            command.StateRevisionId,
            command.EnvironmentRevisionId,
            command.GameAdapterId,
            stored.ObjectKey,
            stored.ByteSize,
            NormalizeSha256(stored.Sha256),
            command.EnvironmentManifest,
            _utcNow(),
            cancellationToken);

    private async Task<PrivateSnapshotTransferRecord?> LoadOwnedTransferAsync(
        StewardAuthenticatedCaller caller,
        PrivateSnapshotTransferId transferId,
        CancellationToken cancellationToken)
    {
        var transfer = await _transfers.LoadAsync(transferId, cancellationToken);
        if (transfer is null)
        {
            return null;
        }

        return string.Equals(
                   transfer.OwnerProvider,
                   caller.Identity.Subject.Provider,
                   StringComparison.Ordinal) &&
               string.Equals(
                   transfer.OwnerExternalId,
                   caller.Identity.Subject.ExternalId,
                   StringComparison.Ordinal) &&
               string.Equals(
                   transfer.SourceInstallationId,
                   caller.InstallationId,
                   StringComparison.Ordinal)
            ? transfer
            : null;
    }

    private async Task SetTerminalStateAsync(
        PrivateSnapshotTransferRecord transfer,
        PrivateSnapshotTransferState state,
        DateTimeOffset changedAt,
        CancellationToken cancellationToken)
    {
        _ = await _transfers.TrySetStateAsync(
            transfer.Id,
            transfer.OwnerProvider,
            transfer.OwnerExternalId,
            transfer.SourceInstallationId,
            PrivateSnapshotTransferState.Active,
            state,
            changedAt,
            cancellationToken);
    }

    private bool IsValidBeginCommand(BeginPrivateSnapshotUploadCommand command)
    {
        if (command.WorldId.Value == Guid.Empty ||
            command.StateRevisionId.Value == Guid.Empty ||
            command.EnvironmentRevisionId.Value == Guid.Empty ||
            command.ExpectedByteSize <= 0 ||
            command.ExpectedByteSize > _options.MaximumPackageBytes ||
            !IsValidSha256(command.ExpectedSha256))
        {
            return false;
        }

        try
        {
            new OwnedWorldSnapshot(
                command.WorldId,
                "validation",
                "validation",
                "validation",
                command.StateRevisionId,
                command.EnvironmentRevisionId,
                command.GameAdapterId,
                "validation",
                command.ExpectedByteSize,
                NormalizeSha256(command.ExpectedSha256),
                command.EnvironmentManifest,
                _utcNow()).Validate();
            return true;
        }
        catch (InvalidDataException)
        {
            return false;
        }
    }

    private static string CreateObjectKey(
        UserIdentity owner,
        string installationId,
        BeginPrivateSnapshotUploadCommand command)
    {
        var ownerNamespace = HashNamespace($"{owner.Provider}\u001f{owner.ExternalId}");
        var installationNamespace = HashNamespace(installationId);
        return $"private-snapshots/{ownerNamespace}/{command.WorldId.Value:N}/{installationNamespace}/{command.StateRevisionId.Value:N}/{command.EnvironmentRevisionId.Value:N}/{NormalizeSha256(command.ExpectedSha256).ToLowerInvariant()}.package";
    }

    private static string HashNamespace(string value)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))
            .ToLowerInvariant();

    private static string CreateProvisioningPlaceholder(PrivateSnapshotTransferId transferId)
        => $"pending:{transferId.Value:N}";

    private static bool MatchesTransferIntent(
        PrivateSnapshotTransferRecord current,
        PrivateSnapshotTransferRecord expected)
        => string.Equals(current.OwnerProvider, expected.OwnerProvider, StringComparison.Ordinal) &&
           string.Equals(current.OwnerExternalId, expected.OwnerExternalId, StringComparison.Ordinal) &&
           string.Equals(current.SourceInstallationId, expected.SourceInstallationId, StringComparison.Ordinal) &&
           current.WorldId == expected.WorldId &&
           current.StateRevisionId == expected.StateRevisionId &&
           current.EnvironmentRevisionId == expected.EnvironmentRevisionId &&
           string.Equals(current.GameAdapterId, expected.GameAdapterId, StringComparison.Ordinal) &&
           string.Equals(current.ObjectKey, expected.ObjectKey, StringComparison.Ordinal) &&
           current.ExpectedByteSize == expected.ExpectedByteSize &&
           string.Equals(current.ExpectedSha256, expected.ExpectedSha256, StringComparison.OrdinalIgnoreCase) &&
           ManifestEquals(current.EnvironmentManifest, expected.EnvironmentManifest) &&
           current.PartSizeBytes == expected.PartSizeBytes &&
           current.PartCount == expected.PartCount;

    private static bool MatchesPublishedSnapshot(
        OwnedWorldSnapshot snapshot,
        string objectKey,
        BeginPrivateSnapshotUploadCommand command)
        => string.Equals(snapshot.GameAdapterId, command.GameAdapterId, StringComparison.Ordinal) &&
           string.Equals(snapshot.StatePackageObjectKey, objectKey, StringComparison.Ordinal) &&
           snapshot.StatePackageByteSize == command.ExpectedByteSize &&
           string.Equals(snapshot.StatePackageSha256, command.ExpectedSha256, StringComparison.OrdinalIgnoreCase) &&
           ManifestEquals(snapshot.EnvironmentManifest, command.EnvironmentManifest);

    private static bool ManifestEquals(EnvironmentManifest left, EnvironmentManifest right)
        => JsonNode.DeepEquals(
            JsonSerializer.SerializeToNode(left, ManifestJsonOptions),
            JsonSerializer.SerializeToNode(right, ManifestJsonOptions));

    private static bool MatchesExpectedObject(
        ImmutableStoredObject stored,
        string objectKey,
        long expectedByteSize,
        string expectedSha256)
        => string.Equals(stored.ObjectKey, objectKey, StringComparison.Ordinal) &&
           stored.ByteSize == expectedByteSize &&
           string.Equals(stored.Sha256, expectedSha256, StringComparison.OrdinalIgnoreCase);

    private static AuthorizePrivateSnapshotDownloadStatus MapDownloadStatus(
        BringHereAvailability availability)
        => availability switch
        {
            BringHereAvailability.Unavailable => AuthorizePrivateSnapshotDownloadStatus.Unavailable,
            BringHereAvailability.AlreadyHere => AuthorizePrivateSnapshotDownloadStatus.AlreadyHere,
            BringHereAvailability.Conflict => AuthorizePrivateSnapshotDownloadStatus.Conflict,
            _ => AuthorizePrivateSnapshotDownloadStatus.NotFoundOrUnauthorized
        };

    private static UserIdentity ToUserIdentity(VerifiedExternalIdentity identity)
        => new(
            identity.Subject.Provider,
            identity.Subject.ExternalId,
            identity.DisplayName);

    private static bool SameOwner(
        OwnedInstallationRegistration installation,
        UserIdentity owner)
        => string.Equals(
               installation.OwnerProvider,
               owner.Provider,
               StringComparison.Ordinal) &&
           string.Equals(
               installation.OwnerExternalId,
               owner.ExternalId,
               StringComparison.Ordinal);

    private static bool IsValidSha256(string value)
        => !string.IsNullOrWhiteSpace(value) &&
           value.Length == 64 &&
           value.All(Uri.IsHexDigit);

    private static string NormalizeSha256(string value) => value.ToUpperInvariant();

    private static DateTimeOffset Min(DateTimeOffset left, DateTimeOffset right)
        => left <= right ? left : right;

    private static void ValidateTransferId(PrivateSnapshotTransferId transferId)
    {
        if (transferId.Value == Guid.Empty)
        {
            throw new ArgumentException("Private snapshot transfer ID is required.", nameof(transferId));
        }
    }
}
