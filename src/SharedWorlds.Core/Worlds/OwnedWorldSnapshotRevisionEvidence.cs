using SharedWorlds.Core.Domain;
using SharedWorlds.Core.Environment;

namespace SharedWorlds.Core.Worlds;

public enum OwnedWorldSnapshotRevisionEvidenceWriteResult
{
    Created,
    NoChange,
    Conflict
}

/// <summary>
/// Immutable owner-private evidence for the exact revision records behind one verified snapshot.
/// Bytes, revision IDs, and an environment manifest are insufficient to reconstruct an existing
/// revision safely: parent links, creation metadata, actor attribution, and state package identity
/// are part of the immutable record bound to those IDs.
/// </summary>
public sealed record OwnedWorldSnapshotRevisionEvidence(
    string OwnerProvider,
    string OwnerExternalId,
    string InstallationId,
    WorldId WorldId,
    StateRevision StateRevision,
    EnvironmentRevision EnvironmentRevision,
    DateTimeOffset RecordedAt)
{
    public const int MaximumStatePackageIdLength = 1_024;

    public void Validate()
    {
        ValidateText(OwnerProvider, "Revision-evidence owner provider", 128);
        ValidateText(OwnerExternalId, "Revision-evidence owner external ID", 256);
        ValidateText(
            InstallationId,
            "Revision-evidence installation ID",
            OwnedWorldSnapshot.MaximumInstallationIdLength);
        if (WorldId.Value == Guid.Empty)
        {
            throw new InvalidDataException(
                "Revision evidence requires a non-empty World ID.");
        }

        ArgumentNullException.ThrowIfNull(StateRevision);
        ArgumentNullException.ThrowIfNull(EnvironmentRevision);
        if (StateRevision.Id.Value == Guid.Empty ||
            EnvironmentRevision.Id.Value == Guid.Empty)
        {
            throw new InvalidDataException(
                "Revision evidence requires non-empty state and environment revision IDs.");
        }

        if (StateRevision.WorldId != WorldId ||
            EnvironmentRevision.WorldId != WorldId)
        {
            throw new InvalidDataException(
                "Revision evidence state and environment records must belong to its exact World.");
        }

        if (StateRevision.EnvironmentRevisionId != EnvironmentRevision.Id)
        {
            throw new InvalidDataException(
                "Revision evidence state record must reference its exact environment revision.");
        }

        ValidateText(
            StateRevision.AdapterId,
            "Revision-evidence state adapter ID",
            OwnedWorldSnapshot.MaximumGameAdapterIdLength);
        ValidateText(
            StateRevision.StatePackageId,
            "Revision-evidence state package ID",
            MaximumStatePackageIdLength);
        if (StateRevision.CreatedAt == default ||
            EnvironmentRevision.CreatedAt == default ||
            RecordedAt == default)
        {
            throw new InvalidDataException(
                "Revision evidence requires state, environment, and evidence timestamps.");
        }

        ValidateParent(
            StateRevision.ParentRevisionId,
            StateRevision.Id,
            "state");
        ValidateParent(
            EnvironmentRevision.ParentRevisionId,
            EnvironmentRevision.Id,
            "environment");

        var manifest = EnvironmentRevision.Manifest
            ?? throw new InvalidDataException(
                "Revision evidence requires an exact environment manifest.");
        var manifestValidator = new OwnedWorldSnapshot(
            WorldId,
            OwnerProvider,
            OwnerExternalId,
            InstallationId,
            StateRevision.Id,
            EnvironmentRevision.Id,
            StateRevision.AdapterId,
            "revision-evidence-validation",
            1,
            new string('0', 64),
            manifest,
            RecordedAt);
        manifestValidator.Validate();
    }

    public void ValidateAgainst(OwnedWorldSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        Validate();
        snapshot.Validate();

        if (!string.Equals(OwnerProvider, snapshot.OwnerProvider, StringComparison.Ordinal) ||
            !string.Equals(OwnerExternalId, snapshot.OwnerExternalId, StringComparison.Ordinal) ||
            !string.Equals(InstallationId, snapshot.InstallationId, StringComparison.Ordinal) ||
            WorldId != snapshot.WorldId ||
            StateRevision.Id != snapshot.StateRevisionId ||
            EnvironmentRevision.Id != snapshot.EnvironmentRevisionId ||
            !string.Equals(
                StateRevision.AdapterId,
                snapshot.GameAdapterId,
                StringComparison.Ordinal) ||
            !ManifestEquals(
                EnvironmentRevision.Manifest,
                snapshot.EnvironmentManifest))
        {
            throw new InvalidDataException(
                "Exact revision evidence disagrees with its verified private snapshot descriptor.");
        }
    }

    private static void ValidateParent(
        RevisionId? parentRevisionId,
        RevisionId revisionId,
        string kind)
    {
        if (parentRevisionId is not { } parent)
        {
            return;
        }

        if (parent.Value == Guid.Empty || parent == revisionId)
        {
            throw new InvalidDataException(
                $"Revision-evidence {kind} parent must be non-empty and different from the revision itself.");
        }
    }

    private static bool ManifestEquals(
        EnvironmentManifest? left,
        EnvironmentManifest? right)
    {
        if (left is null || right is null ||
            left.SchemaVersion != right.SchemaVersion ||
            !string.Equals(left.AdapterId, right.AdapterId, StringComparison.Ordinal) ||
            !string.Equals(left.GameVersion, right.GameVersion, StringComparison.Ordinal) ||
            left.Components is null || right.Components is null ||
            left.Components.Count != right.Components.Count ||
            !DictionaryEquals(left.Configuration, right.Configuration))
        {
            return false;
        }

        for (var index = 0; index < left.Components.Count; index++)
        {
            var leftComponent = left.Components[index];
            var rightComponent = right.Components[index];
            if (leftComponent is null || rightComponent is null ||
                !string.Equals(leftComponent.Kind, rightComponent.Kind, StringComparison.Ordinal) ||
                !string.Equals(leftComponent.Id, rightComponent.Id, StringComparison.Ordinal) ||
                !string.Equals(leftComponent.Version, rightComponent.Version, StringComparison.Ordinal) ||
                !string.Equals(leftComponent.Source, rightComponent.Source, StringComparison.Ordinal) ||
                !DictionaryEquals(leftComponent.Metadata, rightComponent.Metadata))
            {
                return false;
            }
        }

        return true;
    }

    private static bool DictionaryEquals(
        IReadOnlyDictionary<string, string>? left,
        IReadOnlyDictionary<string, string>? right)
    {
        if (ReferenceEquals(left, right))
        {
            return true;
        }

        if (left is null || right is null || left.Count != right.Count)
        {
            return false;
        }

        foreach (var pair in left)
        {
            if (!right.TryGetValue(pair.Key, out var value) ||
                !string.Equals(pair.Value, value, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private static void ValidateText(
        string value,
        string name,
        int maximumLength)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            value.Length > maximumLength ||
            value.Any(char.IsControl))
        {
            throw new InvalidDataException(
                $"{name} must be printable, non-empty, and at most {maximumLength} characters.");
        }
    }
}

public sealed record OwnedWorldSnapshotRevisionEvidenceWriteDecision(
    OwnedWorldSnapshotRevisionEvidenceWriteResult Result,
    OwnedWorldSnapshotRevisionEvidence Current,
    string Reason);

/// <summary>
/// Additive immutable persistence boundary for exact revision records. Implementations must keep one
/// evidence object per owner/World/source-installation/state/environment key. Identical writes are
/// idempotent; divergent records for the same key are conflicts.
/// </summary>
public interface IOwnedWorldSnapshotRevisionEvidenceStore
{
    Task<OwnedWorldSnapshotRevisionEvidence?> LoadExactAsync(
        string ownerProvider,
        string ownerExternalId,
        WorldId worldId,
        string installationId,
        RevisionId stateRevisionId,
        RevisionId environmentRevisionId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<OwnedWorldSnapshotRevisionEvidence>> ListWorldEvidenceAsync(
        string ownerProvider,
        string ownerExternalId,
        WorldId worldId,
        int maximumEvidence,
        CancellationToken cancellationToken = default);

    Task<OwnedWorldSnapshotRevisionEvidenceWriteDecision> PublishAsync(
        OwnedWorldSnapshotRevisionEvidence evidence,
        CancellationToken cancellationToken = default);
}

public sealed record BringHereMaterializationDecision(
    BringHereAvailability Availability,
    OwnedWorldLocationClaim? Source,
    OwnedWorldSnapshot? Snapshot,
    OwnedWorldSnapshotRevisionEvidence? RevisionEvidence,
    IReadOnlyList<OwnedWorldLocationClaim> ConflictingClaims,
    string Reason);

/// <summary>
/// Adds exact immutable revision-record readiness above existing Bring Here head and byte authority.
/// A byte-ready snapshot remains non-materializable until exactly one matching revision-evidence object
/// exists. Legacy descriptors therefore fail closed instead of causing metadata fabrication.
/// </summary>
public sealed class BringHereMaterializationAuthorityService
{
    public const int MaximumRevisionEvidencePerWorld = 10_000;

    private readonly BringHereSnapshotAuthorityService _snapshotAuthority = new();

    public BringHereMaterializationDecision Resolve(
        WorldId worldId,
        UserIdentity authenticatedOwner,
        string targetInstallationId,
        IEnumerable<OwnedWorldLocationClaim> claims,
        IEnumerable<OwnedWorldSnapshot> snapshots,
        IEnumerable<OwnedWorldSnapshotRevisionEvidence> revisionEvidence)
    {
        ArgumentNullException.ThrowIfNull(revisionEvidence);
        var snapshotDecision = _snapshotAuthority.Resolve(
            worldId,
            authenticatedOwner,
            targetInstallationId,
            claims,
            snapshots);
        if (snapshotDecision.Availability != BringHereAvailability.Available)
        {
            return new BringHereMaterializationDecision(
                snapshotDecision.Availability,
                snapshotDecision.Source,
                snapshotDecision.Snapshot,
                RevisionEvidence: null,
                snapshotDecision.ConflictingClaims,
                snapshotDecision.Reason);
        }

        var source = snapshotDecision.Source
            ?? throw new InvalidDataException(
                "Bring Here snapshot authority returned Available without a source claim.");
        var snapshot = snapshotDecision.Snapshot
            ?? throw new InvalidDataException(
                "Bring Here snapshot authority returned Available without a snapshot descriptor.");
        var ownedEvidence = revisionEvidence
            .Where(evidence => SameOwner(evidence, authenticatedOwner))
            .Where(evidence => evidence.WorldId == worldId)
            .Take(MaximumRevisionEvidencePerWorld + 1)
            .ToArray();
        if (ownedEvidence.Length > MaximumRevisionEvidencePerWorld)
        {
            throw new InvalidOperationException(
                $"Private World '{worldId}' exceeded the bounded limit of {MaximumRevisionEvidencePerWorld} revision-evidence objects.");
        }

        var exact = ownedEvidence
            .Where(evidence => string.Equals(
                evidence.InstallationId,
                source.InstallationId,
                StringComparison.Ordinal))
            .Where(evidence => evidence.StateRevision.Id == source.StateRevisionId)
            .Where(evidence => evidence.EnvironmentRevision.Id == source.EnvironmentRevisionId)
            .ToArray();
        if (exact.Length == 0)
        {
            return new BringHereMaterializationDecision(
                BringHereAvailability.Unavailable,
                source,
                snapshot,
                RevisionEvidence: null,
                ConflictingClaims: [],
                "The exact remote bytes are verified, but the immutable state and environment revision records required for safe materialization are not available yet.");
        }

        if (exact.Length > 1)
        {
            throw new InvalidDataException(
                "The exact private World source head has duplicate revision-evidence objects.");
        }

        var selected = exact[0];
        selected.ValidateAgainst(snapshot);
        return new BringHereMaterializationDecision(
            BringHereAvailability.Available,
            source,
            snapshot,
            selected,
            ConflictingClaims: [],
            "One unambiguous remote head has exact verified bytes and immutable revision-record evidence.");
    }

    private static bool SameOwner(
        OwnedWorldSnapshotRevisionEvidence evidence,
        UserIdentity owner)
        => string.Equals(
               evidence.OwnerProvider,
               owner.Provider,
               StringComparison.Ordinal) &&
           string.Equals(
               evidence.OwnerExternalId,
               owner.ExternalId,
               StringComparison.Ordinal);
}
