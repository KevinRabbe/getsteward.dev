using SharedWorlds.Backend.Identity;
using SharedWorlds.Backend.Transfers;
using SharedWorlds.Core.Domain;
using SharedWorlds.Core.Environment;
using SharedWorlds.Core.Worlds;
using Xunit;

namespace SharedWorlds.Backend.Tests.Transfers;

public sealed class PrivateSnapshotRevisionEvidenceServiceTests
{
    private static readonly DateTimeOffset ServerNow =
        new(2026, 8, 4, 1, 0, 0, TimeSpan.Zero);
    private static readonly UserIdentity Owner = new("steam", "owner-1", "Owner");
    private const string PackageSha256 =
        "0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF";

    [Fact]
    public async Task ExactPublicationUsesAuthenticatedOwnerInstallationAndServerTime()
    {
        var fixture = Fixture.Create();

        var result = await fixture.Service.PublishAsync(
            fixture.Caller,
            fixture.Command);

        Assert.Equal(
            PublishPrivateSnapshotRevisionEvidenceStatus.Published,
            result.Status);
        var evidence = Assert.IsType<OwnedWorldSnapshotRevisionEvidence>(result.Evidence);
        Assert.Equal(Owner.Provider, evidence.OwnerProvider);
        Assert.Equal(Owner.ExternalId, evidence.OwnerExternalId);
        Assert.Equal(fixture.Caller.InstallationId, evidence.InstallationId);
        Assert.Equal(ServerNow, evidence.RecordedAt);
        Assert.Equal(fixture.Command.StateRevision, evidence.StateRevision);
        Assert.Equal(
            fixture.Command.EnvironmentRevision,
            evidence.EnvironmentRevision);
    }

    [Fact]
    public async Task RepeatedPublicationRepairsAmbiguousResponseIdempotently()
    {
        var fixture = Fixture.Create();

        var first = await fixture.Service.PublishAsync(
            fixture.Caller,
            fixture.Command);
        var repeated = await fixture.Service.PublishAsync(
            fixture.Caller,
            fixture.Command);

        Assert.Equal(
            PublishPrivateSnapshotRevisionEvidenceStatus.Published,
            first.Status);
        Assert.Equal(
            PublishPrivateSnapshotRevisionEvidenceStatus.AlreadyPublished,
            repeated.Status);
        Assert.Single(fixture.EvidenceStore.Evidence);
    }

    [Fact]
    public async Task MissingExactSnapshotIsIndistinguishableAndNeverWritesEvidence()
    {
        var fixture = Fixture.Create(includeSnapshot: false);

        var result = await fixture.Service.PublishAsync(
            fixture.Caller,
            fixture.Command);

        Assert.Equal(
            PublishPrivateSnapshotRevisionEvidenceStatus.NotFoundOrUnauthorized,
            result.Status);
        Assert.Null(result.Evidence);
        Assert.Empty(fixture.EvidenceStore.Evidence);
    }

    [Fact]
    public async Task ForeignAuthenticatedOwnerCannotPublishSourceEvidence()
    {
        var fixture = Fixture.Create();
        var foreign = Caller(
            new UserIdentity("steam", "owner-2", "Other"),
            fixture.Caller.InstallationId);

        var result = await fixture.Service.PublishAsync(foreign, fixture.Command);

        Assert.Equal(
            PublishPrivateSnapshotRevisionEvidenceStatus.NotFoundOrUnauthorized,
            result.Status);
        Assert.Empty(fixture.EvidenceStore.Evidence);
    }

    [Fact]
    public async Task MalformedRevisionRecordsReturnInvalidRequestBeforeStoreAccess()
    {
        var fixture = Fixture.Create();
        var malformed = fixture.Command with
        {
            WorldId = WorldId.New()
        };

        var result = await fixture.Service.PublishAsync(
            fixture.Caller,
            malformed);

        Assert.Equal(
            PublishPrivateSnapshotRevisionEvidenceStatus.InvalidRequest,
            result.Status);
        Assert.Equal(0, fixture.SnapshotStore.LoadCalls);
        Assert.Empty(fixture.EvidenceStore.Evidence);
    }

    [Fact]
    public async Task DescriptorDisagreementReturnsConflictBeforeEvidenceWrite()
    {
        var fixture = Fixture.Create();
        var palworldManifest = fixture.Command.EnvironmentRevision.Manifest with
        {
            AdapterId = "palworld"
        };
        var conflict = fixture.Command with
        {
            StateRevision = fixture.Command.StateRevision with
            {
                AdapterId = "palworld"
            },
            EnvironmentRevision = fixture.Command.EnvironmentRevision with
            {
                Manifest = palworldManifest
            }
        };

        var result = await fixture.Service.PublishAsync(
            fixture.Caller,
            conflict);

        Assert.Equal(
            PublishPrivateSnapshotRevisionEvidenceStatus.Conflict,
            result.Status);
        Assert.Null(result.Evidence);
        Assert.Empty(fixture.EvidenceStore.Evidence);
    }

    [Fact]
    public async Task ExistingDifferentImmutableRecordReturnsConflict()
    {
        var fixture = Fixture.Create();
        var current = fixture.ExpectedEvidence with
        {
            StateRevision = fixture.ExpectedEvidence.StateRevision with
            {
                StatePackageId = "different-existing-package"
            }
        };
        fixture.EvidenceStore.Evidence.Add(current);

        var result = await fixture.Service.PublishAsync(
            fixture.Caller,
            fixture.Command);

        Assert.Equal(
            PublishPrivateSnapshotRevisionEvidenceStatus.Conflict,
            result.Status);
        Assert.Equal(current, result.Evidence);
        Assert.Single(fixture.EvidenceStore.Evidence);
    }

    private static StewardAuthenticatedCaller Caller(
        UserIdentity owner,
        string installationId)
        => new(
            new VerifiedExternalIdentity(
                new ExternalIdentityRef(owner.Provider, owner.ExternalId),
                owner.DisplayName),
            installationId,
            new StewardSessionId(Guid.NewGuid()));

    private static EnvironmentManifest Manifest()
        => new(
            SchemaVersion: 1,
            AdapterId: "factorio",
            GameVersion: "2.0.0",
            Components: [],
            Configuration: new Dictionary<string, string>());

    private sealed class Fixture
    {
        private Fixture(
            StewardAuthenticatedCaller caller,
            PublishPrivateSnapshotRevisionEvidenceCommand command,
            OwnedWorldSnapshot snapshot,
            SnapshotStore snapshotStore,
            EvidenceStore evidenceStore,
            PrivateSnapshotRevisionEvidenceService service)
        {
            Caller = caller;
            Command = command;
            Snapshot = snapshot;
            SnapshotStore = snapshotStore;
            EvidenceStore = evidenceStore;
            Service = service;
        }

        public StewardAuthenticatedCaller Caller { get; }
        public PublishPrivateSnapshotRevisionEvidenceCommand Command { get; }
        public OwnedWorldSnapshot Snapshot { get; }
        public SnapshotStore SnapshotStore { get; }
        public EvidenceStore EvidenceStore { get; }
        public PrivateSnapshotRevisionEvidenceService Service { get; }

        public OwnedWorldSnapshotRevisionEvidence ExpectedEvidence => new(
            Owner.Provider,
            Owner.ExternalId,
            Caller.InstallationId,
            Command.WorldId,
            Command.StateRevision,
            Command.EnvironmentRevision,
            ServerNow);

        public static Fixture Create(bool includeSnapshot = true)
        {
            var caller = PrivateSnapshotRevisionEvidenceServiceTests.Caller(
                Owner,
                "pc-a");
            var worldId = WorldId.New();
            var stateId = RevisionId.New();
            var environmentId = RevisionId.New();
            var manifest = Manifest();
            var state = new StateRevision(
                stateId,
                worldId,
                ParentRevisionId: RevisionId.New(),
                CreatedAt: ServerNow.AddMinutes(-2),
                CreatedBy: Owner,
                AdapterId: "factorio",
                StatePackageId: "state-package-id",
                EnvironmentRevisionId: environmentId);
            var environment = new EnvironmentRevision(
                environmentId,
                worldId,
                ParentRevisionId: RevisionId.New(),
                CreatedAt: ServerNow.AddMinutes(-3),
                CreatedBy: Owner,
                Manifest: manifest);
            var command = new PublishPrivateSnapshotRevisionEvidenceCommand(
                worldId,
                state,
                environment);
            var snapshot = new OwnedWorldSnapshot(
                worldId,
                Owner.Provider,
                Owner.ExternalId,
                caller.InstallationId,
                stateId,
                environmentId,
                "factorio",
                "private/state/package",
                100,
                PackageSha256,
                manifest,
                ServerNow.AddMinutes(-1));
            var snapshotStore = new SnapshotStore
            {
                Snapshot = includeSnapshot ? snapshot : null
            };
            var evidenceStore = new EvidenceStore();
            var service = new PrivateSnapshotRevisionEvidenceService(
                snapshotStore,
                evidenceStore,
                () => ServerNow);
            return new Fixture(
                caller,
                command,
                snapshot,
                snapshotStore,
                evidenceStore,
                service);
        }
    }

    private sealed class SnapshotStore : IOwnedWorldSnapshotStore
    {
        public OwnedWorldSnapshot? Snapshot { get; init; }
        public int LoadCalls { get; private set; }

        public Task<OwnedWorldSnapshot?> LoadExactAsync(
            string ownerProvider,
            string ownerExternalId,
            WorldId worldId,
            string installationId,
            RevisionId stateRevisionId,
            RevisionId environmentRevisionId,
            CancellationToken cancellationToken = default)
        {
            LoadCalls++;
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
            => throw new NotSupportedException();

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
            => throw new NotSupportedException();

        public Task<IReadOnlyList<OwnedWorldSnapshotRevisionEvidence>>
            ListWorldEvidenceAsync(
                string ownerProvider,
                string ownerExternalId,
                WorldId worldId,
                int maximumEvidence,
                CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

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

        private static bool SameKey(
            OwnedWorldSnapshotRevisionEvidence left,
            OwnedWorldSnapshotRevisionEvidence right)
            => string.Equals(left.OwnerProvider, right.OwnerProvider, StringComparison.Ordinal) &&
               string.Equals(left.OwnerExternalId, right.OwnerExternalId, StringComparison.Ordinal) &&
               string.Equals(left.InstallationId, right.InstallationId, StringComparison.Ordinal) &&
               left.WorldId == right.WorldId &&
               left.StateRevision.Id == right.StateRevision.Id &&
               left.EnvironmentRevision.Id == right.EnvironmentRevision.Id;
    }
}
