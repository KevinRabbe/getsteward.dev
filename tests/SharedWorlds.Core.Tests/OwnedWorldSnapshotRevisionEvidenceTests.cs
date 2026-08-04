using SharedWorlds.Core.Domain;
using SharedWorlds.Core.Environment;
using SharedWorlds.Core.Worlds;
using Xunit;

namespace SharedWorlds.Core.Tests;

public sealed class OwnedWorldSnapshotRevisionEvidenceTests
{
    private static readonly UserIdentity Owner = new("steam", "owner-1", "Owner");
    private static readonly DateTimeOffset CreatedAt =
        new(2026, 8, 4, 0, 0, 0, TimeSpan.Zero);
    private const string PackageSha256 =
        "0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF";

    [Fact]
    public void ExactRevisionRecordsValidateAgainstSnapshotDescriptor()
    {
        var fixture = CreateFixture();

        var exception = Record.Exception(() =>
            fixture.Evidence.ValidateAgainst(fixture.Snapshot));

        Assert.Null(exception);
    }

    [Fact]
    public void SnapshotCrossCheckRejectsDifferentAdapterForSameRevisionId()
    {
        var fixture = CreateFixture();
        var changed = fixture.Evidence with
        {
            StateRevision = fixture.Evidence.StateRevision with
            {
                AdapterId = "palworld"
            },
            EnvironmentRevision = fixture.Evidence.EnvironmentRevision with
            {
                Manifest = fixture.Evidence.EnvironmentRevision.Manifest with
                {
                    AdapterId = "palworld"
                }
            }
        };

        var exception = Assert.Throws<InvalidDataException>(() =>
            changed.ValidateAgainst(fixture.Snapshot));

        Assert.Contains(
            "disagrees with its verified private snapshot descriptor",
            exception.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void StateMustReferenceExactEnvironmentRecord()
    {
        var fixture = CreateFixture();
        var changed = fixture.Evidence with
        {
            StateRevision = fixture.Evidence.StateRevision with
            {
                EnvironmentRevisionId = RevisionId.New()
            }
        };

        var exception = Assert.Throws<InvalidDataException>(changed.Validate);

        Assert.Contains(
            "reference its exact environment revision",
            exception.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void SelfParentAndMissingTimestampsFailClosed()
    {
        var fixture = CreateFixture();
        var selfParent = fixture.Evidence with
        {
            StateRevision = fixture.Evidence.StateRevision with
            {
                ParentRevisionId = fixture.Evidence.StateRevision.Id
            }
        };
        var missingTimestamp = fixture.Evidence with
        {
            EnvironmentRevision = fixture.Evidence.EnvironmentRevision with
            {
                CreatedAt = default
            }
        };

        Assert.Throws<InvalidDataException>(selfParent.Validate);
        Assert.Throws<InvalidDataException>(missingTimestamp.Validate);
    }

    [Fact]
    public async Task RevisionEvidenceRequiresExistingVerifiedSnapshotBytes()
    {
        var fixture = CreateFixture();
        var snapshots = new SnapshotStore();
        var evidenceStore = new EvidenceStore();
        var registry = new OwnedWorldSnapshotRevisionEvidenceRegistry(
            snapshots,
            evidenceStore);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            registry.PublishAsync(Owner, fixture.Evidence));

        Assert.Contains(
            "snapshot bytes must exist",
            exception.Message,
            StringComparison.Ordinal);
        Assert.Empty(evidenceStore.Evidence);
    }

    [Fact]
    public async Task ExactEvidenceIsIdempotentAndDivergentRecordConflicts()
    {
        var fixture = CreateFixture();
        var snapshots = new SnapshotStore { Snapshot = fixture.Snapshot };
        var evidenceStore = new EvidenceStore();
        var registry = new OwnedWorldSnapshotRevisionEvidenceRegistry(
            snapshots,
            evidenceStore);

        var created = await registry.PublishAsync(Owner, fixture.Evidence);
        var repeated = await registry.PublishAsync(Owner, fixture.Evidence);
        var changed = fixture.Evidence with
        {
            StateRevision = fixture.Evidence.StateRevision with
            {
                StatePackageId = "different-package-identity"
            }
        };
        var conflict = await registry.PublishAsync(Owner, changed);

        Assert.Equal(
            OwnedWorldSnapshotRevisionEvidenceWriteResult.Created,
            created.Result);
        Assert.Equal(
            OwnedWorldSnapshotRevisionEvidenceWriteResult.NoChange,
            repeated.Result);
        Assert.Equal(
            OwnedWorldSnapshotRevisionEvidenceWriteResult.Conflict,
            conflict.Result);
        Assert.Equal(fixture.Evidence, conflict.Current);
        Assert.Single(evidenceStore.Evidence);
    }

    [Fact]
    public void ByteReadyLegacySnapshotWithoutRevisionEvidenceIsNotMaterializable()
    {
        var fixture = CreateFixture();
        var authority = new BringHereMaterializationAuthorityService();

        var decision = authority.Resolve(
            fixture.WorldId,
            Owner,
            "pc-b",
            [fixture.Claim],
            [fixture.Snapshot],
            revisionEvidence: []);

        Assert.Equal(BringHereAvailability.Unavailable, decision.Availability);
        Assert.Equal(fixture.Claim, decision.Source);
        Assert.Equal(fixture.Snapshot, decision.Snapshot);
        Assert.Null(decision.RevisionEvidence);
        Assert.Contains(
            "immutable state and environment revision records",
            decision.Reason,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ExactBytesAndRevisionEvidenceMakeHeadMaterializationReady()
    {
        var fixture = CreateFixture();
        var authority = new BringHereMaterializationAuthorityService();

        var decision = authority.Resolve(
            fixture.WorldId,
            Owner,
            "pc-b",
            [fixture.Claim],
            [fixture.Snapshot],
            [fixture.Evidence]);

        Assert.Equal(BringHereAvailability.Available, decision.Availability);
        Assert.Equal(fixture.Claim, decision.Source);
        Assert.Equal(fixture.Snapshot, decision.Snapshot);
        Assert.Equal(fixture.Evidence, decision.RevisionEvidence);
    }

    [Fact]
    public void ForeignAndStaleEvidenceCannotSatisfySelectedHead()
    {
        var fixture = CreateFixture();
        var foreign = fixture.Evidence with { OwnerExternalId = "owner-2" };
        var staleState = fixture.Evidence.StateRevision with { Id = RevisionId.New() };
        var stale = fixture.Evidence with { StateRevision = staleState };
        var authority = new BringHereMaterializationAuthorityService();

        var decision = authority.Resolve(
            fixture.WorldId,
            Owner,
            "pc-b",
            [fixture.Claim],
            [fixture.Snapshot],
            [foreign, stale]);

        Assert.Equal(BringHereAvailability.Unavailable, decision.Availability);
        Assert.Null(decision.RevisionEvidence);
    }

    [Fact]
    public void DuplicateExactRevisionEvidenceFailsClosed()
    {
        var fixture = CreateFixture();
        var authority = new BringHereMaterializationAuthorityService();

        var exception = Assert.Throws<InvalidDataException>(() => authority.Resolve(
            fixture.WorldId,
            Owner,
            "pc-b",
            [fixture.Claim],
            [fixture.Snapshot],
            [fixture.Evidence, fixture.Evidence]));

        Assert.Contains(
            "duplicate revision-evidence",
            exception.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ExistingHeadConflictRemainsConflictBeforeEvidenceSelection()
    {
        var fixture = CreateFixture();
        var otherState = RevisionId.New();
        var otherEnvironment = RevisionId.New();
        var conflict = fixture.Claim with
        {
            InstallationId = "pc-c",
            StateRevisionId = otherState,
            EnvironmentRevisionId = otherEnvironment
        };
        var authority = new BringHereMaterializationAuthorityService();

        var decision = authority.Resolve(
            fixture.WorldId,
            Owner,
            "pc-b",
            [fixture.Claim, conflict],
            [fixture.Snapshot],
            [fixture.Evidence]);

        Assert.Equal(BringHereAvailability.Conflict, decision.Availability);
        Assert.Null(decision.RevisionEvidence);
        Assert.Equal(2, decision.ConflictingClaims.Count);
    }

    private static Fixture CreateFixture()
    {
        var worldId = WorldId.New();
        var stateId = RevisionId.New();
        var environmentId = RevisionId.New();
        var manifest = Manifest();
        var snapshot = new OwnedWorldSnapshot(
            worldId,
            Owner.Provider,
            Owner.ExternalId,
            "pc-a",
            stateId,
            environmentId,
            "factorio",
            "private/state/package",
            100,
            PackageSha256,
            manifest,
            CreatedAt.AddMinutes(1));
        var state = new StateRevision(
            stateId,
            worldId,
            ParentRevisionId: RevisionId.New(),
            CreatedAt,
            Owner,
            "factorio",
            "state-package-id",
            environmentId);
        var environment = new EnvironmentRevision(
            environmentId,
            worldId,
            ParentRevisionId: RevisionId.New(),
            CreatedAt.AddSeconds(-1),
            Owner,
            manifest);
        var evidence = new OwnedWorldSnapshotRevisionEvidence(
            Owner.Provider,
            Owner.ExternalId,
            "pc-a",
            worldId,
            state,
            environment,
            CreatedAt.AddMinutes(1));
        var claim = new OwnedWorldLocationClaim(
            worldId,
            Owner.Provider,
            Owner.ExternalId,
            "pc-a",
            stateId,
            environmentId,
            CreatedAt)
        {
            Presentation = new OwnedWorldPresentation("Factory World", "factorio")
        };
        return new Fixture(worldId, claim, snapshot, evidence);
    }

    private static bool SameKey(
        OwnedWorldSnapshotRevisionEvidence left,
        OwnedWorldSnapshotRevisionEvidence right)
        => string.Equals(left.OwnerProvider, right.OwnerProvider, StringComparison.Ordinal) &&
           string.Equals(left.OwnerExternalId, right.OwnerExternalId, StringComparison.Ordinal) &&
           string.Equals(left.InstallationId, right.InstallationId, StringComparison.Ordinal) &&
           left.WorldId == right.WorldId &&
           left.StateRevision.Id == right.StateRevision.Id &&
           left.EnvironmentRevision.Id == right.EnvironmentRevision.Id;

    private static EnvironmentManifest Manifest()
        => new(
            SchemaVersion: 1,
            AdapterId: "factorio",
            GameVersion: "2.0.0",
            Components:
            [
                new EnvironmentComponent(
                    "mod",
                    "example",
                    "1.0.0",
                    "workshop",
                    new Dictionary<string, string>
                    {
                        ["hash"] = "abc"
                    })
            ],
            Configuration: new Dictionary<string, string>
            {
                ["difficulty"] = "normal"
            });

    private sealed record Fixture(
        WorldId WorldId,
        OwnedWorldLocationClaim Claim,
        OwnedWorldSnapshot Snapshot,
        OwnedWorldSnapshotRevisionEvidence Evidence);

    private sealed class SnapshotStore : IOwnedWorldSnapshotStore
    {
        public OwnedWorldSnapshot? Snapshot { get; init; }

        public Task<OwnedWorldSnapshot?> LoadExactAsync(
            string ownerProvider,
            string ownerExternalId,
            WorldId worldId,
            string installationId,
            RevisionId stateRevisionId,
            RevisionId environmentRevisionId,
            CancellationToken cancellationToken = default)
        {
            var snapshot = Snapshot;
            if (snapshot is null ||
                !string.Equals(snapshot.OwnerProvider, ownerProvider, StringComparison.Ordinal) ||
                !string.Equals(snapshot.OwnerExternalId, ownerExternalId, StringComparison.Ordinal) ||
                !string.Equals(snapshot.InstallationId, installationId, StringComparison.Ordinal) ||
                snapshot.WorldId != worldId ||
                snapshot.StateRevisionId != stateRevisionId ||
                snapshot.EnvironmentRevisionId != environmentRevisionId)
            {
                return Task.FromResult<OwnedWorldSnapshot?>(null);
            }

            return Task.FromResult<OwnedWorldSnapshot?>(snapshot);
        }

        public Task<IReadOnlyList<OwnedWorldSnapshot>> ListWorldSnapshotsAsync(
            string ownerProvider,
            string ownerExternalId,
            WorldId worldId,
            int maximumSnapshots,
            CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<OwnedWorldSnapshot>>(
                Snapshot is { } snapshot &&
                snapshot.WorldId == worldId &&
                string.Equals(snapshot.OwnerProvider, ownerProvider, StringComparison.Ordinal) &&
                string.Equals(snapshot.OwnerExternalId, ownerExternalId, StringComparison.Ordinal)
                    ? [snapshot]
                    : []);

        public Task<OwnedWorldSnapshotWriteDecision> PublishAsync(
            OwnedWorldSnapshot snapshot,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }

    private sealed class EvidenceStore : IOwnedWorldSnapshotRevisionEvidenceStore
    {
        public List<OwnedWorldSnapshotRevisionEvidence> Evidence { get; } = [];

        public Task<OwnedWorldSnapshotRevisionEvidence?> LoadExactAsync(
            string ownerProvider,
            string ownerExternalId,
            WorldId worldId,
            string installationId,
            RevisionId stateRevisionId,
            RevisionId environmentRevisionId,
            CancellationToken cancellationToken = default)
            => Task.FromResult(Evidence.SingleOrDefault(evidence =>
                string.Equals(evidence.OwnerProvider, ownerProvider, StringComparison.Ordinal) &&
                string.Equals(evidence.OwnerExternalId, ownerExternalId, StringComparison.Ordinal) &&
                string.Equals(evidence.InstallationId, installationId, StringComparison.Ordinal) &&
                evidence.WorldId == worldId &&
                evidence.StateRevision.Id == stateRevisionId &&
                evidence.EnvironmentRevision.Id == environmentRevisionId));

        public Task<IReadOnlyList<OwnedWorldSnapshotRevisionEvidence>> ListWorldEvidenceAsync(
            string ownerProvider,
            string ownerExternalId,
            WorldId worldId,
            int maximumEvidence,
            CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<OwnedWorldSnapshotRevisionEvidence>>(
                Evidence
                    .Where(evidence => evidence.WorldId == worldId)
                    .Where(evidence => string.Equals(
                        evidence.OwnerProvider,
                        ownerProvider,
                        StringComparison.Ordinal))
                    .Where(evidence => string.Equals(
                        evidence.OwnerExternalId,
                        ownerExternalId,
                        StringComparison.Ordinal))
                    .Take(maximumEvidence)
                    .ToArray());

        public Task<OwnedWorldSnapshotRevisionEvidenceWriteDecision> PublishAsync(
            OwnedWorldSnapshotRevisionEvidence evidence,
            CancellationToken cancellationToken = default)
        {
            var existing = Evidence.SingleOrDefault(candidate => SameKey(candidate, evidence));
            if (existing is null)
            {
                Evidence.Add(evidence);
                return Task.FromResult(new OwnedWorldSnapshotRevisionEvidenceWriteDecision(
                    OwnedWorldSnapshotRevisionEvidenceWriteResult.Created,
                    evidence,
                    "Created."));
            }

            if (existing == evidence)
            {
                return Task.FromResult(new OwnedWorldSnapshotRevisionEvidenceWriteDecision(
                    OwnedWorldSnapshotRevisionEvidenceWriteResult.NoChange,
                    existing,
                    "No change."));
            }

            return Task.FromResult(new OwnedWorldSnapshotRevisionEvidenceWriteDecision(
                OwnedWorldSnapshotRevisionEvidenceWriteResult.Conflict,
                existing,
                "Conflict."));
        }
    }
}
