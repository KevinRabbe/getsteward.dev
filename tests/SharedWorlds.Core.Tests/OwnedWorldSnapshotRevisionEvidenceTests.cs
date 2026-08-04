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
    public void SameRevisionIdWithDifferentImmutableMetadataFailsClosed()
    {
        var fixture = CreateFixture();
        var changedState = fixture.Evidence.StateRevision with
        {
            StatePackageId = "different-package-identity"
        };
        var changed = fixture.Evidence with { StateRevision = changedState };

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
}
