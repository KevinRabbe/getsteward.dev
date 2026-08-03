using SharedWorlds.Core.Domain;
using SharedWorlds.Core.Environment;
using SharedWorlds.Core.Worlds;

namespace SharedWorlds.Core.Tests;

public sealed class OwnedWorldSnapshotRegistryTests
{
    private static readonly UserIdentity Owner = new("steam", "owner-1", "Owner");
    private static readonly UserIdentity OtherOwner = new("steam", "owner-2", "Other");
    private const string PackageSha256 =
        "0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF";

    [Fact]
    public async Task SnapshotRequiresRegisteredOwnedSourceInstallation()
    {
        var store = new MemoryStore();
        var registry = new OwnedWorldSnapshotRegistry(store, store);

        var missing = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            registry.PublishAsync(
                Owner,
                "pc-a",
                WorldId.New(),
                RevisionId.New(),
                RevisionId.New(),
                "factorio",
                "private/state/package",
                100,
                PackageSha256,
                Manifest(),
                DateTimeOffset.UtcNow));
        Assert.Contains("must be registered", missing.Message, StringComparison.Ordinal);

        await RegisterAsync(store, OtherOwner, "pc-a");
        var foreign = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            registry.PublishAsync(
                Owner,
                "pc-a",
                WorldId.New(),
                RevisionId.New(),
                RevisionId.New(),
                "factorio",
                "private/state/package",
                100,
                PackageSha256,
                Manifest(),
                DateTimeOffset.UtcNow));
        Assert.Contains("does not own", foreign.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SnapshotMustMatchExactAdvertisedSourceHead()
    {
        var store = new MemoryStore();
        var worldId = WorldId.New();
        var stateId = RevisionId.New();
        var environmentId = RevisionId.New();
        await RegisterAndLocateAsync(store, worldId, stateId, environmentId);
        var registry = new OwnedWorldSnapshotRegistry(store, store);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            registry.PublishAsync(
                Owner,
                "pc-a",
                worldId,
                RevisionId.New(),
                environmentId,
                "factorio",
                "private/state/package",
                100,
                PackageSha256,
                Manifest(),
                DateTimeOffset.UtcNow));

        Assert.Contains("exact currently advertised", exception.Message, StringComparison.Ordinal);
        Assert.Empty(store.Snapshots);
    }

    [Fact]
    public async Task SnapshotAdapterMustAgreeWithLocationAndEnvironmentManifest()
    {
        var store = new MemoryStore();
        var worldId = WorldId.New();
        var stateId = RevisionId.New();
        var environmentId = RevisionId.New();
        await RegisterAndLocateAsync(store, worldId, stateId, environmentId);
        var registry = new OwnedWorldSnapshotRegistry(store, store);

        var locationMismatch = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            registry.PublishAsync(
                Owner,
                "pc-a",
                worldId,
                stateId,
                environmentId,
                "palworld",
                "private/state/package",
                100,
                PackageSha256,
                Manifest("palworld"),
                DateTimeOffset.UtcNow));
        Assert.Contains("location presentation", locationMismatch.Message, StringComparison.Ordinal);

        var manifestMismatch = await Assert.ThrowsAsync<InvalidDataException>(() =>
            registry.PublishAsync(
                Owner,
                "pc-a",
                worldId,
                stateId,
                environmentId,
                "factorio",
                "private/state/package",
                100,
                PackageSha256,
                Manifest("palworld"),
                DateTimeOffset.UtcNow));
        Assert.Contains("different game adapter", manifestMismatch.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExactSnapshotPublicationIsImmutableAndIdempotent()
    {
        var store = new MemoryStore();
        var worldId = WorldId.New();
        var stateId = RevisionId.New();
        var environmentId = RevisionId.New();
        await RegisterAndLocateAsync(store, worldId, stateId, environmentId);
        var registry = new OwnedWorldSnapshotRegistry(store, store);
        var manifest = Manifest();
        var publishedAt = DateTimeOffset.UtcNow;

        var created = await registry.PublishAsync(
            Owner,
            "pc-a",
            worldId,
            stateId,
            environmentId,
            "factorio",
            "private/state/package",
            100,
            PackageSha256.ToLowerInvariant(),
            manifest,
            publishedAt);
        var repeated = await registry.PublishAsync(
            Owner,
            "pc-a",
            worldId,
            stateId,
            environmentId,
            "factorio",
            "private/state/package",
            100,
            PackageSha256,
            manifest,
            publishedAt);
        var conflict = await registry.PublishAsync(
            Owner,
            "pc-a",
            worldId,
            stateId,
            environmentId,
            "factorio",
            "private/state/package",
            101,
            PackageSha256,
            manifest,
            publishedAt);

        Assert.Equal(OwnedWorldSnapshotWriteResult.Created, created.Result);
        Assert.Equal(OwnedWorldSnapshotWriteResult.NoChange, repeated.Result);
        Assert.Equal(OwnedWorldSnapshotWriteResult.Conflict, conflict.Result);
        Assert.Equal(100, conflict.Current.StatePackageByteSize);
        Assert.Equal(PackageSha256, created.Current.StatePackageSha256);
        Assert.Single(store.Snapshots);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(21474836481)]
    public async Task SnapshotPackageSizeIsBounded(long byteSize)
    {
        var store = new MemoryStore();
        var worldId = WorldId.New();
        var stateId = RevisionId.New();
        var environmentId = RevisionId.New();
        await RegisterAndLocateAsync(store, worldId, stateId, environmentId);
        var registry = new OwnedWorldSnapshotRegistry(store, store);

        await Assert.ThrowsAsync<InvalidDataException>(() => registry.PublishAsync(
            Owner,
            "pc-a",
            worldId,
            stateId,
            environmentId,
            "factorio",
            "private/state/package",
            byteSize,
            PackageSha256,
            Manifest(),
            DateTimeOffset.UtcNow));
    }

    [Fact]
    public async Task InvalidSnapshotHashFailsBeforePersistence()
    {
        var store = new MemoryStore();
        var worldId = WorldId.New();
        var stateId = RevisionId.New();
        var environmentId = RevisionId.New();
        await RegisterAndLocateAsync(store, worldId, stateId, environmentId);
        var registry = new OwnedWorldSnapshotRegistry(store, store);

        await Assert.ThrowsAsync<InvalidDataException>(() => registry.PublishAsync(
            Owner,
            "pc-a",
            worldId,
            stateId,
            environmentId,
            "factorio",
            "private/state/package",
            100,
            "not-a-hash",
            Manifest(),
            DateTimeOffset.UtcNow));
        Assert.Empty(store.Snapshots);
    }

    [Fact]
    public void KnownRemoteHeadWithoutExactSnapshotIsNotTransferable()
    {
        var worldId = WorldId.New();
        var stateId = RevisionId.New();
        var environmentId = RevisionId.New();
        var claim = Claim(worldId, stateId, environmentId);
        var service = new BringHereSnapshotAuthorityService();

        var decision = service.Resolve(
            worldId,
            Owner,
            "pc-b",
            [claim],
            snapshots: []);

        Assert.Equal(BringHereAvailability.Unavailable, decision.Availability);
        Assert.Equal(claim, decision.Source);
        Assert.Null(decision.Snapshot);
        Assert.Contains("not available yet", decision.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void ExactVerifiedSnapshotMakesUnambiguousRemoteHeadTransferable()
    {
        var worldId = WorldId.New();
        var stateId = RevisionId.New();
        var environmentId = RevisionId.New();
        var claim = Claim(worldId, stateId, environmentId);
        var snapshot = Snapshot(worldId, stateId, environmentId);
        var service = new BringHereSnapshotAuthorityService();

        var decision = service.Resolve(
            worldId,
            Owner,
            "pc-b",
            [claim],
            [snapshot]);

        Assert.Equal(BringHereAvailability.Available, decision.Availability);
        Assert.Equal(claim, decision.Source);
        Assert.Equal(snapshot, decision.Snapshot);
    }

    [Fact]
    public void StaleAndForeignSnapshotsCannotSatisfySelectedSource()
    {
        var worldId = WorldId.New();
        var stateId = RevisionId.New();
        var environmentId = RevisionId.New();
        var claim = Claim(worldId, stateId, environmentId);
        var stale = Snapshot(worldId, RevisionId.New(), environmentId);
        var foreign = Snapshot(worldId, stateId, environmentId) with
        {
            OwnerExternalId = OtherOwner.ExternalId
        };
        var service = new BringHereSnapshotAuthorityService();

        var decision = service.Resolve(
            worldId,
            Owner,
            "pc-b",
            [claim],
            [stale, foreign]);

        Assert.Equal(BringHereAvailability.Unavailable, decision.Availability);
        Assert.Null(decision.Snapshot);
    }

    [Fact]
    public void DuplicateExactSnapshotDescriptorsFailClosed()
    {
        var worldId = WorldId.New();
        var stateId = RevisionId.New();
        var environmentId = RevisionId.New();
        var claim = Claim(worldId, stateId, environmentId);
        var snapshot = Snapshot(worldId, stateId, environmentId);
        var service = new BringHereSnapshotAuthorityService();

        var exception = Assert.Throws<InvalidDataException>(() => service.Resolve(
            worldId,
            Owner,
            "pc-b",
            [claim],
            [snapshot, snapshot]));

        Assert.Contains("duplicate snapshot", exception.Message, StringComparison.Ordinal);
    }

    private static async Task RegisterAndLocateAsync(
        MemoryStore store,
        WorldId worldId,
        RevisionId stateId,
        RevisionId environmentId)
    {
        await RegisterAsync(store, Owner, "pc-a");
        var registry = new OwnedWorldLocationRegistry(store);
        var result = await registry.PublishLocationWithPresentationAsync(
            Owner,
            "pc-a",
            worldId,
            stateId,
            environmentId,
            "Factory World",
            "factorio",
            DateTimeOffset.UtcNow);
        Assert.Equal(OwnedWorldLocationWriteResult.Created, result.Result);
    }

    private static async Task RegisterAsync(
        MemoryStore store,
        UserIdentity owner,
        string installationId)
    {
        var registry = new OwnedWorldLocationRegistry(store);
        await registry.RegisterInstallationAsync(
            owner,
            installationId,
            installationId,
            DateTimeOffset.UtcNow);
    }

    private static OwnedWorldLocationClaim Claim(
        WorldId worldId,
        RevisionId stateId,
        RevisionId environmentId)
        => new(
            worldId,
            Owner.Provider,
            Owner.ExternalId,
            "pc-a",
            stateId,
            environmentId,
            DateTimeOffset.UtcNow)
        {
            Presentation = new OwnedWorldPresentation("Factory World", "factorio")
        };

    private static OwnedWorldSnapshot Snapshot(
        WorldId worldId,
        RevisionId stateId,
        RevisionId environmentId)
        => new(
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
            Manifest(),
            DateTimeOffset.UtcNow);

    private static EnvironmentManifest Manifest(string adapterId = "factorio")
        => new(
            SchemaVersion: 1,
            adapterId,
            GameVersion: "2.0.0",
            Components: [],
            Configuration: new Dictionary<string, string>());

    private sealed class MemoryStore : IOwnedWorldLocationStore, IOwnedWorldSnapshotStore
    {
        public Dictionary<string, OwnedInstallationRegistration> Installations { get; } =
            new(StringComparer.Ordinal);
        public List<OwnedWorldSnapshot> Snapshots { get; } = [];

        private Dictionary<(WorldId WorldId, string Provider, string ExternalId, string InstallationId), OwnedWorldLocationClaim> Locations { get; } = [];

        public Task<OwnedInstallationRegistration?> GetInstallationAsync(
            string installationId,
            CancellationToken cancellationToken = default)
            => Task.FromResult(Installations.GetValueOrDefault(installationId));

        public Task RegisterInstallationAsync(
            OwnedInstallationRegistration registration,
            CancellationToken cancellationToken = default)
        {
            Installations[registration.InstallationId] = registration;
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<OwnedWorldLocationClaim>> ListWorldLocationsAsync(
            WorldId worldId,
            string ownerProvider,
            string ownerExternalId,
            CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<OwnedWorldLocationClaim>>(
                Locations.Values
                    .Where(location => location.WorldId == worldId)
                    .Where(location => string.Equals(
                        location.OwnerProvider,
                        ownerProvider,
                        StringComparison.Ordinal))
                    .Where(location => string.Equals(
                        location.OwnerExternalId,
                        ownerExternalId,
                        StringComparison.Ordinal))
                    .ToArray());

        public Task<OwnedWorldLocationWriteDecision> CompareExchangeLocationAsync(
            OwnedWorldLocationClaim desired,
            RevisionId? expectedStateRevisionId,
            RevisionId? expectedEnvironmentRevisionId,
            CancellationToken cancellationToken = default)
        {
            var key = (
                desired.WorldId,
                desired.OwnerProvider,
                desired.OwnerExternalId,
                desired.InstallationId);
            Locations.TryGetValue(key, out var current);
            if (current is not null)
            {
                return Task.FromResult(new OwnedWorldLocationWriteDecision(
                    OwnedWorldLocationWriteResult.Conflict,
                    current,
                    "Already exists."));
            }

            Locations[key] = desired;
            return Task.FromResult(new OwnedWorldLocationWriteDecision(
                OwnedWorldLocationWriteResult.Created,
                desired,
                "Created."));
        }

        public Task<OwnedWorldLocationWriteDecision> RemoveLocationAsync(
            WorldId worldId,
            string ownerProvider,
            string ownerExternalId,
            string installationId,
            RevisionId expectedStateRevisionId,
            RevisionId expectedEnvironmentRevisionId,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<OwnedWorldSnapshot?> LoadExactAsync(
            string ownerProvider,
            string ownerExternalId,
            WorldId worldId,
            string installationId,
            RevisionId stateRevisionId,
            RevisionId environmentRevisionId,
            CancellationToken cancellationToken = default)
            => Task.FromResult(Snapshots.SingleOrDefault(snapshot =>
                SameKey(
                    snapshot,
                    ownerProvider,
                    ownerExternalId,
                    worldId,
                    installationId,
                    stateRevisionId,
                    environmentRevisionId)));

        public Task<IReadOnlyList<OwnedWorldSnapshot>> ListWorldSnapshotsAsync(
            string ownerProvider,
            string ownerExternalId,
            WorldId worldId,
            int maximumSnapshots,
            CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<OwnedWorldSnapshot>>(
                Snapshots
                    .Where(snapshot => snapshot.WorldId == worldId)
                    .Where(snapshot => string.Equals(
                        snapshot.OwnerProvider,
                        ownerProvider,
                        StringComparison.Ordinal))
                    .Where(snapshot => string.Equals(
                        snapshot.OwnerExternalId,
                        ownerExternalId,
                        StringComparison.Ordinal))
                    .Take(maximumSnapshots)
                    .ToArray());

        public Task<OwnedWorldSnapshotWriteDecision> PublishAsync(
            OwnedWorldSnapshot snapshot,
            CancellationToken cancellationToken = default)
        {
            var existing = Snapshots.SingleOrDefault(candidate => SameKey(
                candidate,
                snapshot.OwnerProvider,
                snapshot.OwnerExternalId,
                snapshot.WorldId,
                snapshot.InstallationId,
                snapshot.StateRevisionId,
                snapshot.EnvironmentRevisionId));
            if (existing is null)
            {
                Snapshots.Add(snapshot);
                return Task.FromResult(new OwnedWorldSnapshotWriteDecision(
                    OwnedWorldSnapshotWriteResult.Created,
                    snapshot,
                    "Created."));
            }

            if (existing == snapshot)
            {
                return Task.FromResult(new OwnedWorldSnapshotWriteDecision(
                    OwnedWorldSnapshotWriteResult.NoChange,
                    existing,
                    "Unchanged."));
            }

            return Task.FromResult(new OwnedWorldSnapshotWriteDecision(
                OwnedWorldSnapshotWriteResult.Conflict,
                existing,
                "The exact snapshot key already has different immutable metadata."));
        }

        private static bool SameKey(
            OwnedWorldSnapshot snapshot,
            string ownerProvider,
            string ownerExternalId,
            WorldId worldId,
            string installationId,
            RevisionId stateRevisionId,
            RevisionId environmentRevisionId)
            => snapshot.WorldId == worldId &&
               snapshot.StateRevisionId == stateRevisionId &&
               snapshot.EnvironmentRevisionId == environmentRevisionId &&
               string.Equals(snapshot.OwnerProvider, ownerProvider, StringComparison.Ordinal) &&
               string.Equals(snapshot.OwnerExternalId, ownerExternalId, StringComparison.Ordinal) &&
               string.Equals(snapshot.InstallationId, installationId, StringComparison.Ordinal);
    }
}
