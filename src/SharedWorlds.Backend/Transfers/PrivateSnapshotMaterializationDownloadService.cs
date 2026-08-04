using SharedWorlds.Backend.Identity;
using SharedWorlds.Core.Domain;
using SharedWorlds.Core.Environment;
using SharedWorlds.Core.Worlds;

namespace SharedWorlds.Backend.Transfers;

/// <summary>
/// Target-visible materialization plan. The backend object key remains private; exact immutable
/// revision records are included because revision IDs and bytes alone cannot safely recreate them.
/// </summary>
public sealed record PrivateSnapshotMaterializationDownloadPlan(
    WorldId WorldId,
    string SourceInstallationId,
    RevisionId StateRevisionId,
    RevisionId EnvironmentRevisionId,
    string GameAdapterId,
    long ExpectedByteSize,
    string ExpectedSha256,
    EnvironmentManifest EnvironmentManifest,
    StateRevision StateRevision,
    EnvironmentRevision EnvironmentRevision,
    DirectObjectTransferAuthorization Authorization);

public sealed record AuthorizePrivateSnapshotMaterializationDownloadResult(
    AuthorizePrivateSnapshotDownloadStatus Status,
    PrivateSnapshotMaterializationDownloadPlan? Plan,
    string Reason);

/// <summary>
/// Issues owner-private download authorization only when one exact remote head has verified immutable
/// bytes and matching exact revision-record evidence. The object store never selects a head and is not
/// consulted for byte-only legacy descriptors that cannot be safely materialized.
/// </summary>
public sealed class PrivateSnapshotMaterializationDownloadService
{
    private readonly IOwnedWorldLocationStore _locations;
    private readonly IOwnedWorldSnapshotStore _snapshots;
    private readonly IOwnedWorldSnapshotRevisionEvidenceStore _revisionEvidence;
    private readonly IPrivateImmutableObjectStore _objectStore;
    private readonly BringHereSnapshotAuthorityService _snapshotAuthority = new();
    private readonly BringHereMaterializationAuthorityService _materializationAuthority = new();
    private readonly PrivateSnapshotTransferOptions _options;
    private readonly Func<DateTimeOffset> _utcNow;

    public PrivateSnapshotMaterializationDownloadService(
        IOwnedWorldLocationStore locations,
        IOwnedWorldSnapshotStore snapshots,
        IOwnedWorldSnapshotRevisionEvidenceStore revisionEvidence,
        IPrivateImmutableObjectStore objectStore,
        Func<DateTimeOffset> utcNow,
        PrivateSnapshotTransferOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(locations);
        ArgumentNullException.ThrowIfNull(snapshots);
        ArgumentNullException.ThrowIfNull(revisionEvidence);
        ArgumentNullException.ThrowIfNull(objectStore);
        ArgumentNullException.ThrowIfNull(utcNow);
        _locations = locations;
        _snapshots = snapshots;
        _revisionEvidence = revisionEvidence;
        _objectStore = objectStore;
        _utcNow = utcNow;
        _options = options ?? PrivateSnapshotTransferOptions.FirstReleaseDefaults;
    }

    public async Task<AuthorizePrivateSnapshotMaterializationDownloadResult> AuthorizeAsync(
        StewardAuthenticatedCaller caller,
        WorldId worldId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(caller);
        if (worldId.Value == Guid.Empty || string.IsNullOrWhiteSpace(caller.InstallationId))
        {
            return NotFound();
        }

        var owner = ToUserIdentity(caller.Identity);
        var target = await _locations.GetInstallationAsync(
            caller.InstallationId,
            cancellationToken);
        if (target is null || !SameOwner(target, owner))
        {
            return NotFound();
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
        var byteDecision = _snapshotAuthority.Resolve(
            worldId,
            owner,
            caller.InstallationId,
            claims,
            snapshots);
        if (byteDecision.Availability != BringHereAvailability.Available)
        {
            return new(
                MapStatus(byteDecision.Availability),
                Plan: null,
                byteDecision.Reason);
        }

        var source = byteDecision.Source
            ?? throw new InvalidDataException(
                "Bring Here byte authority returned Available without a source claim.");
        var snapshot = byteDecision.Snapshot
            ?? throw new InvalidDataException(
                "Bring Here byte authority returned Available without a snapshot descriptor.");
        var evidence = await _revisionEvidence.LoadExactAsync(
            owner.Provider,
            owner.ExternalId,
            worldId,
            source.InstallationId,
            source.StateRevisionId,
            source.EnvironmentRevisionId,
            cancellationToken);
        var materialization = _materializationAuthority.Resolve(
            worldId,
            owner,
            caller.InstallationId,
            claims,
            snapshots,
            evidence is null ? [] : [evidence]);
        if (materialization.Availability != BringHereAvailability.Available)
        {
            return new(
                MapStatus(materialization.Availability),
                Plan: null,
                materialization.Reason);
        }

        var selectedSnapshot = materialization.Snapshot
            ?? throw new InvalidDataException(
                "Materialization authority returned Available without a snapshot descriptor.");
        var selectedEvidence = materialization.RevisionEvidence
            ?? throw new InvalidDataException(
                "Materialization authority returned Available without exact revision evidence.");
        if (selectedSnapshot != snapshot)
        {
            throw new InvalidDataException(
                "Materialization authority selected a different snapshot than byte authority.");
        }

        var stored = await _objectStore.InspectObjectAsync(
            selectedSnapshot.StatePackageObjectKey,
            cancellationToken);
        if (stored is null || !MatchesExpectedObject(stored, selectedSnapshot))
        {
            return new(
                AuthorizePrivateSnapshotDownloadStatus.StorageIntegrityFailure,
                Plan: null,
                "The verified private snapshot object is missing or does not match its immutable descriptor.");
        }

        var authorization = await _objectStore.AuthorizeDownloadAsync(
            selectedSnapshot.StatePackageObjectKey,
            selectedSnapshot.StatePackageByteSize,
            _utcNow() + _options.AuthorizationLifetime,
            cancellationToken);
        return new(
            AuthorizePrivateSnapshotDownloadStatus.Authorized,
            new PrivateSnapshotMaterializationDownloadPlan(
                selectedSnapshot.WorldId,
                selectedSnapshot.InstallationId,
                selectedSnapshot.StateRevisionId,
                selectedSnapshot.EnvironmentRevisionId,
                selectedSnapshot.GameAdapterId,
                selectedSnapshot.StatePackageByteSize,
                selectedSnapshot.StatePackageSha256,
                selectedSnapshot.EnvironmentManifest,
                selectedEvidence.StateRevision,
                selectedEvidence.EnvironmentRevision,
                authorization),
            materialization.Reason);
    }

    private static AuthorizePrivateSnapshotMaterializationDownloadResult NotFound()
        => new(
            AuthorizePrivateSnapshotDownloadStatus.NotFoundOrUnauthorized,
            Plan: null,
            "The private World was not found or is not authorized.");

    private static AuthorizePrivateSnapshotDownloadStatus MapStatus(
        BringHereAvailability availability)
        => availability switch
        {
            BringHereAvailability.Unavailable => AuthorizePrivateSnapshotDownloadStatus.Unavailable,
            BringHereAvailability.AlreadyHere => AuthorizePrivateSnapshotDownloadStatus.AlreadyHere,
            BringHereAvailability.Conflict => AuthorizePrivateSnapshotDownloadStatus.Conflict,
            _ => AuthorizePrivateSnapshotDownloadStatus.NotFoundOrUnauthorized
        };

    private static bool MatchesExpectedObject(
        ImmutableStoredObject stored,
        OwnedWorldSnapshot snapshot)
        => string.Equals(
               stored.ObjectKey,
               snapshot.StatePackageObjectKey,
               StringComparison.Ordinal) &&
           stored.ByteSize == snapshot.StatePackageByteSize &&
           string.Equals(
               stored.Sha256,
               snapshot.StatePackageSha256,
               StringComparison.OrdinalIgnoreCase);

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
}
