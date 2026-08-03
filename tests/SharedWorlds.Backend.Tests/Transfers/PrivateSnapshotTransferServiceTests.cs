using System.Security.Cryptography;
using SharedWorlds.Backend.Identity;
using SharedWorlds.Backend.Transfers;
using SharedWorlds.Core.Domain;
using SharedWorlds.Core.Environment;
using SharedWorlds.Core.Worlds;
using Xunit;

namespace SharedWorlds.Backend.Tests.Transfers;

public sealed class PrivateSnapshotTransferServiceTests
{
    private static readonly DateTimeOffset Start =
        new(2026, 8, 4, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task BeginRequiresExactCurrentOwnedSourceBeforeAllocatingObjectUpload()
    {
        var fixture = await Fixture.CreateAsync();
        var wrongInstallation = fixture.Caller("pc-b");
        var wrongHead = fixture.Command([1, 2, 3]) with
        {
            StateRevisionId = RevisionId.New()
        };

        var foreignSource = await fixture.Service.BeginUploadAsync(
            wrongInstallation,
            fixture.Command([1, 2, 3]));
        var staleHead = await fixture.Service.BeginUploadAsync(
            fixture.Source,
            wrongHead);

        Assert.Equal(BeginPrivateSnapshotUploadStatus.NotFoundOrUnauthorized, foreignSource.Status);
        Assert.Equal(BeginPrivateSnapshotUploadStatus.NotFoundOrUnauthorized, staleHead.Status);
        Assert.Empty(fixture.ObjectStore.Uploads);
        Assert.Empty(fixture.TransferStore.Records);
    }

    [Fact]
    public async Task UploadPlanIsResumableInstallationPrivateAndUsesExactPartLengths()
    {
        var options = new PrivateSnapshotTransferOptions(
            maximumPackageBytes: 1024,
            partSizeBytes: 4,
            transferLifetime: TimeSpan.FromHours(24),
            authorizationLifetime: TimeSpan.FromMinutes(15));
        var fixture = await Fixture.CreateAsync(options);
        var command = fixture.Command([1, 2, 3, 4, 5, 6, 7, 8, 9, 10]);

        var firstBegin = await fixture.Service.BeginUploadAsync(fixture.Source, command);
        var repeatedBegin = await fixture.Service.BeginUploadAsync(fixture.Source, command);
        var transfer = Assert.IsType<PrivateSnapshotTransferRecord>(firstBegin.Transfer);
        var repeated = Assert.IsType<PrivateSnapshotTransferRecord>(repeatedBegin.Transfer);
        var first = await fixture.Service.AuthorizePartAsync(fixture.Source, transfer.Id, 1);
        var last = await fixture.Service.AuthorizePartAsync(fixture.Source, transfer.Id, 3);
        var invalid = await fixture.Service.AuthorizePartAsync(fixture.Source, transfer.Id, 4);
        var otherInstallation = await fixture.Service.AuthorizePartAsync(
            fixture.Caller("pc-b"),
            transfer.Id,
            1);

        Assert.Equal(BeginPrivateSnapshotUploadStatus.Started, firstBegin.Status);
        Assert.Equal(transfer.Id, repeated.Id);
        Assert.Equal(3, transfer.PartCount);
        Assert.Equal(4, Assert.IsType<DirectObjectTransferAuthorization>(first.Authorization).ExpectedByteSize);
        Assert.Equal(2, Assert.IsType<DirectObjectTransferAuthorization>(last.Authorization).ExpectedByteSize);
        Assert.Equal(AuthorizePrivateSnapshotPartStatus.InvalidPart, invalid.Status);
        Assert.Equal(AuthorizePrivateSnapshotPartStatus.TransferNotFound, otherInstallation.Status);
        Assert.DoesNotContain(fixture.Owner.ExternalId, transfer.ObjectKey, StringComparison.Ordinal);
        Assert.DoesNotContain(fixture.Source.InstallationId, transfer.ObjectKey, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExactProviderBytesPublishSnapshotWithoutChangingLocationHead()
    {
        var fixture = await Fixture.CreateAsync(SmallParts());
        var bytes = new byte[] { 1, 2, 3, 4, 5 };
        var command = fixture.Command(bytes);
        var begin = await fixture.Service.BeginUploadAsync(fixture.Source, command);
        var transfer = Assert.IsType<PrivateSnapshotTransferRecord>(begin.Transfer);
        fixture.ObjectStore.UploadAll(transfer.ProviderUploadId, bytes, transfer.PartSizeBytes);

        var finalized = await fixture.Service.FinalizeAsync(fixture.Source, transfer.Id);
        var repeated = await fixture.Service.FinalizeAsync(fixture.Source, transfer.Id);
        var claims = await fixture.LocationStore.ListWorldLocationsAsync(
            fixture.WorldId,
            fixture.Owner.Provider,
            fixture.Owner.ExternalId);

        Assert.Equal(FinalizePrivateSnapshotUploadStatus.Finalized, finalized.Status);
        Assert.Equal(FinalizePrivateSnapshotUploadStatus.AlreadyFinalized, repeated.Status);
        var snapshot = Assert.IsType<OwnedWorldSnapshot>(finalized.Snapshot);
        Assert.Equal(command.StateRevisionId, snapshot.StateRevisionId);
        Assert.Equal(command.EnvironmentRevisionId, snapshot.EnvironmentRevisionId);
        Assert.Equal(command.ExpectedSha256, snapshot.StatePackageSha256);
        var claim = Assert.Single(claims);
        Assert.Equal(command.StateRevisionId, claim.StateRevisionId);
        Assert.Equal(command.EnvironmentRevisionId, claim.EnvironmentRevisionId);
    }

    [Fact]
    public async Task IntegrityMismatchNeverPublishesSnapshot()
    {
        var fixture = await Fixture.CreateAsync(SmallParts());
        var command = fixture.Command([1, 2, 3, 4]);
        var begin = await fixture.Service.BeginUploadAsync(fixture.Source, command);
        var transfer = Assert.IsType<PrivateSnapshotTransferRecord>(begin.Transfer);
        fixture.ObjectStore.UploadAll(
            transfer.ProviderUploadId,
            [9, 9, 9, 9],
            transfer.PartSizeBytes);

        var result = await fixture.Service.FinalizeAsync(fixture.Source, transfer.Id);
        var persisted = Assert.IsType<PrivateSnapshotTransferRecord>(
            await fixture.TransferStore.LoadAsync(transfer.Id));

        Assert.Equal(FinalizePrivateSnapshotUploadStatus.IntegrityMismatch, result.Status);
        Assert.Equal(PrivateSnapshotTransferState.IntegrityFailed, persisted.State);
        Assert.Empty(fixture.SnapshotStore.Snapshots);
    }

    [Fact]
    public async Task SourceHeadAdvanceDuringUploadBlocksPublicationAndPreservesNewHead()
    {
        var fixture = await Fixture.CreateAsync(SmallParts());
        var bytes = new byte[] { 1, 2, 3, 4 };
        var command = fixture.Command(bytes);
        var begin = await fixture.Service.BeginUploadAsync(fixture.Source, command);
        var transfer = Assert.IsType<PrivateSnapshotTransferRecord>(begin.Transfer);
        fixture.ObjectStore.UploadAll(transfer.ProviderUploadId, bytes, transfer.PartSizeBytes);

        var nextState = RevisionId.New();
        var nextEnvironment = RevisionId.New();
        var locationRegistry = new OwnedWorldLocationRegistry(fixture.LocationStore);
        var advanced = await locationRegistry.PublishLocationWithPresentationAsync(
            fixture.Owner,
            fixture.Source.InstallationId,
            fixture.WorldId,
            nextState,
            nextEnvironment,
            "Factory World",
            "factorio",
            Start.AddMinutes(1),
            expectedStateRevisionId: fixture.StateRevisionId,
            expectedEnvironmentRevisionId: fixture.EnvironmentRevisionId);

        var result = await fixture.Service.FinalizeAsync(fixture.Source, transfer.Id);
        var claim = Assert.Single(await fixture.LocationStore.ListWorldLocationsAsync(
            fixture.WorldId,
            fixture.Owner.Provider,
            fixture.Owner.ExternalId));

        Assert.Equal(OwnedWorldLocationWriteResult.Updated, advanced.Result);
        Assert.Equal(FinalizePrivateSnapshotUploadStatus.PublicationBlocked, result.Status);
        Assert.Equal(nextState, claim.StateRevisionId);
        Assert.Equal(nextEnvironment, claim.EnvironmentRevisionId);
        Assert.Empty(fixture.SnapshotStore.Snapshots);
    }

    [Fact]
    public async Task TargetDownloadRequiresExactSelectedSnapshotAndHidesObjectKey()
    {
        var fixture = await Fixture.CreateAsync(SmallParts());
        var bytes = new byte[] { 5, 6, 7, 8 };
        await fixture.PublishSnapshotAsync(bytes);
        await fixture.RegisterAsync("pc-b");
        var target = fixture.Caller("pc-b");

        var authorized = await fixture.Service.AuthorizeDownloadAsync(target, fixture.WorldId);
        var sourceAlreadyHere = await fixture.Service.AuthorizeDownloadAsync(
            fixture.Source,
            fixture.WorldId);

        Assert.Equal(AuthorizePrivateSnapshotDownloadStatus.Authorized, authorized.Status);
        var plan = Assert.IsType<PrivateSnapshotDownloadPlan>(authorized.Plan);
        Assert.Equal(fixture.Source.InstallationId, plan.SourceInstallationId);
        Assert.Equal(fixture.StateRevisionId, plan.StateRevisionId);
        Assert.Equal(fixture.EnvironmentRevisionId, plan.EnvironmentRevisionId);
        Assert.Equal(bytes.LongLength, plan.ExpectedByteSize);
        Assert.Equal(Hash(bytes), plan.ExpectedSha256);
        Assert.Equal("GET", plan.Authorization.Method);
        Assert.DoesNotContain("ObjectKey", nameof(PrivateSnapshotDownloadPlan), StringComparison.Ordinal);
        Assert.Equal(AuthorizePrivateSnapshotDownloadStatus.AlreadyHere, sourceAlreadyHere.Status);
        Assert.Null(sourceAlreadyHere.Plan);
    }

    [Fact]
    public async Task DivergentOwnedHeadsNeverReceiveDownloadAuthorization()
    {
        var fixture = await Fixture.CreateAsync(SmallParts());
        await fixture.PublishSnapshotAsync([1, 2, 3, 4]);
        await fixture.RegisterAsync("pc-c");
        var registry = new OwnedWorldLocationRegistry(fixture.LocationStore);
        var second = await registry.PublishLocationWithPresentationAsync(
            fixture.Owner,
            "pc-c",
            fixture.WorldId,
            RevisionId.New(),
            RevisionId.New(),
            "Factory World",
            "factorio",
            Start.AddMinutes(2));
        Assert.Equal(OwnedWorldLocationWriteResult.Created, second.Result);
        await fixture.RegisterAsync("pc-b");

        var result = await fixture.Service.AuthorizeDownloadAsync(
            fixture.Caller("pc-b"),
            fixture.WorldId);

        Assert.Equal(AuthorizePrivateSnapshotDownloadStatus.Conflict, result.Status);
        Assert.Null(result.Plan);
        Assert.Equal(0, fixture.ObjectStore.DownloadAuthorizationCount);
    }

    [Fact]
    public async Task MissingOrCorruptedStoredObjectFailsBeforeDownloadAuthorization()
    {
        var fixture = await Fixture.CreateAsync(SmallParts());
        var bytes = new byte[] { 1, 4, 7, 10 };
        var snapshot = await fixture.PublishSnapshotAsync(bytes);
        await fixture.RegisterAsync("pc-b");
        fixture.ObjectStore.CorruptObject(snapshot.StatePackageObjectKey, [0, 0, 0, 0]);

        var result = await fixture.Service.AuthorizeDownloadAsync(
            fixture.Caller("pc-b"),
            fixture.WorldId);

        Assert.Equal(AuthorizePrivateSnapshotDownloadStatus.StorageIntegrityFailure, result.Status);
        Assert.Null(result.Plan);
        Assert.Equal(0, fixture.ObjectStore.DownloadAuthorizationCount);
    }

    [Fact]
    public async Task ExistingVerifiedObjectRecoversWithoutCreatingMultipartUpload()
    {
        var fixture = await Fixture.CreateAsync(SmallParts());
        var bytes = new byte[] { 3, 1, 4, 1 };
        var command = fixture.Command(bytes);
        var objectKey = fixture.ExpectedObjectKey(command);
        fixture.ObjectStore.SeedObject(objectKey, bytes);

        var result = await fixture.Service.BeginUploadAsync(fixture.Source, command);

        Assert.Equal(BeginPrivateSnapshotUploadStatus.AlreadyPublished, result.Status);
        Assert.Null(result.Transfer);
        Assert.Empty(fixture.ObjectStore.Uploads);
        var snapshot = Assert.Single(fixture.SnapshotStore.Snapshots);
        Assert.Equal(objectKey, snapshot.StatePackageObjectKey);
    }

    private static PrivateSnapshotTransferOptions SmallParts()
        => new(
            maximumPackageBytes: 1024,
            partSizeBytes: 4,
            transferLifetime: TimeSpan.FromHours(24),
            authorizationLifetime: TimeSpan.FromMinutes(15));

    private static string Hash(byte[] bytes)
        => Convert.ToHexString(SHA256.HashData(bytes));

    private sealed class Fixture
    {
        private Fixture(
            MemoryAuthorityStore authorityStore,
            MemoryTransferStore transferStore,
            DirectObjectStore objectStore,
            PrivateSnapshotTransferService service,
            UserIdentity owner,
            StewardAuthenticatedCaller source,
            WorldId worldId,
            RevisionId stateRevisionId,
            RevisionId environmentRevisionId)
        {
            LocationStore = authorityStore;
            SnapshotStore = authorityStore;
            TransferStore = transferStore;
            ObjectStore = objectStore;
            Service = service;
            Owner = owner;
            Source = source;
            WorldId = worldId;
            StateRevisionId = stateRevisionId;
            EnvironmentRevisionId = environmentRevisionId;
        }

        public MemoryAuthorityStore LocationStore { get; }
        public MemoryAuthorityStore SnapshotStore { get; }
        public MemoryTransferStore TransferStore { get; }
        public DirectObjectStore ObjectStore { get; }
        public PrivateSnapshotTransferService Service { get; }
        public UserIdentity Owner { get; }
        public StewardAuthenticatedCaller Source { get; }
        public WorldId WorldId { get; }
        public RevisionId StateRevisionId { get; }
        public RevisionId EnvironmentRevisionId { get; }

        public static async Task<Fixture> CreateAsync(
            PrivateSnapshotTransferOptions? options = null)
        {
            var owner = new UserIdentity("steam", "owner-1", "Owner");
            var source = Caller(owner, "pc-a");
            var worldId = WorldId.New();
            var stateId = RevisionId.New();
            var environmentId = RevisionId.New();
            var authorityStore = new MemoryAuthorityStore();
            var transferStore = new MemoryTransferStore();
            var objectStore = new DirectObjectStore();
            var service = new PrivateSnapshotTransferService(
                authorityStore,
                authorityStore,
                transferStore,
                objectStore,
                () => Start,
                options,
                () => new PrivateSnapshotTransferId(
                    Guid.Parse("11111111-1111-1111-1111-111111111111")));
            var fixture = new Fixture(
                authorityStore,
                transferStore,
                objectStore,
                service,
                owner,
                source,
                worldId,
                stateId,
                environmentId);
            await fixture.RegisterAsync(source.InstallationId);
            var registry = new OwnedWorldLocationRegistry(authorityStore);
            var location = await registry.PublishLocationWithPresentationAsync(
                owner,
                source.InstallationId,
                worldId,
                stateId,
                environmentId,
                "Factory World",
                "factorio",
                Start);
            Assert.Equal(OwnedWorldLocationWriteResult.Created, location.Result);
            return fixture;
        }

        public StewardAuthenticatedCaller Caller(string installationId)
            => Caller(Owner, installationId);

        public async Task RegisterAsync(string installationId)
        {
            var registry = new OwnedWorldLocationRegistry(LocationStore);
            await registry.RegisterInstallationAsync(
                Owner,
                installationId,
                installationId,
                Start);
        }

        public BeginPrivateSnapshotUploadCommand Command(byte[] bytes)
            => new(
                WorldId,
                StateRevisionId,
                EnvironmentRevisionId,
                "factorio",
                bytes.LongLength,
                Hash(bytes),
                Manifest());

        public async Task<OwnedWorldSnapshot> PublishSnapshotAsync(byte[] bytes)
        {
            var command = Command(bytes);
            var begin = await Service.BeginUploadAsync(Source, command);
            var transfer = Assert.IsType<PrivateSnapshotTransferRecord>(begin.Transfer);
            ObjectStore.UploadAll(transfer.ProviderUploadId, bytes, transfer.PartSizeBytes);
            var final = await Service.FinalizeAsync(Source, transfer.Id);
            Assert.Equal(FinalizePrivateSnapshotUploadStatus.Finalized, final.Status);
            return Assert.IsType<OwnedWorldSnapshot>(final.Snapshot);
        }

        public string ExpectedObjectKey(BeginPrivateSnapshotUploadCommand command)
        {
            var ownerNamespace = Convert.ToHexString(SHA256.HashData(
                    System.Text.Encoding.UTF8.GetBytes(
                        $"{Owner.Provider}\u001f{Owner.ExternalId}")))
                .ToLowerInvariant();
            var installationNamespace = Convert.ToHexString(SHA256.HashData(
                    System.Text.Encoding.UTF8.GetBytes(Source.InstallationId)))
                .ToLowerInvariant();
            return $"private-snapshots/{ownerNamespace}/{command.WorldId.Value:N}/{installationNamespace}/{command.StateRevisionId.Value:N}/{command.EnvironmentRevisionId.Value:N}/{command.ExpectedSha256.ToLowerInvariant()}.package";
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
    }

    private sealed class MemoryAuthorityStore :
        IOwnedWorldLocationStore,
        IOwnedWorldSnapshotStore
    {
        private readonly object _gate = new();
        private readonly Dictionary<string, OwnedInstallationRegistration> _installations =
            new(StringComparer.Ordinal);
        private readonly Dictionary<
            (WorldId WorldId, string OwnerProvider, string OwnerExternalId, string InstallationId),
            OwnedWorldLocationClaim> _locations = [];

        public List<OwnedWorldSnapshot> Snapshots { get; } = [];

        public Task<OwnedInstallationRegistration?> GetInstallationAsync(
            string installationId,
            CancellationToken cancellationToken = default)
        {
            lock (_gate)
            {
                return Task.FromResult(_installations.GetValueOrDefault(installationId));
            }
        }

        public Task RegisterInstallationAsync(
            OwnedInstallationRegistration registration,
            CancellationToken cancellationToken = default)
        {
            lock (_gate)
            {
                if (_installations.TryGetValue(registration.InstallationId, out var current) &&
                    (!string.Equals(current.OwnerProvider, registration.OwnerProvider, StringComparison.Ordinal) ||
                     !string.Equals(current.OwnerExternalId, registration.OwnerExternalId, StringComparison.Ordinal)))
                {
                    throw new InvalidOperationException("Installation belongs to another owner.");
                }

                _installations[registration.InstallationId] = registration;
                return Task.CompletedTask;
            }
        }

        public Task<IReadOnlyList<OwnedWorldLocationClaim>> ListWorldLocationsAsync(
            WorldId worldId,
            string ownerProvider,
            string ownerExternalId,
            CancellationToken cancellationToken = default)
        {
            lock (_gate)
            {
                return Task.FromResult<IReadOnlyList<OwnedWorldLocationClaim>>(
                    _locations.Values
                        .Where(claim => claim.WorldId == worldId)
                        .Where(claim => string.Equals(
                            claim.OwnerProvider,
                            ownerProvider,
                            StringComparison.Ordinal))
                        .Where(claim => string.Equals(
                            claim.OwnerExternalId,
                            ownerExternalId,
                            StringComparison.Ordinal))
                        .ToArray());
            }
        }

        public Task<OwnedWorldLocationWriteDecision> CompareExchangeLocationAsync(
            OwnedWorldLocationClaim desired,
            RevisionId? expectedStateRevisionId,
            RevisionId? expectedEnvironmentRevisionId,
            CancellationToken cancellationToken = default)
        {
            lock (_gate)
            {
                var key = (
                    desired.WorldId,
                    desired.OwnerProvider,
                    desired.OwnerExternalId,
                    desired.InstallationId);
                _locations.TryGetValue(key, out var current);
                if (current is null)
                {
                    if (expectedStateRevisionId is not null || expectedEnvironmentRevisionId is not null)
                    {
                        return Task.FromResult(new OwnedWorldLocationWriteDecision(
                            OwnedWorldLocationWriteResult.Conflict,
                            null,
                            "Expected prior location is absent."));
                    }

                    _locations[key] = desired;
                    return Task.FromResult(new OwnedWorldLocationWriteDecision(
                        OwnedWorldLocationWriteResult.Created,
                        desired,
                        "Created."));
                }

                if (current.StateRevisionId == desired.StateRevisionId &&
                    current.EnvironmentRevisionId == desired.EnvironmentRevisionId)
                {
                    var merged = desired with
                    {
                        Presentation = desired.Presentation ?? current.Presentation
                    };
                    _locations[key] = merged;
                    return Task.FromResult(new OwnedWorldLocationWriteDecision(
                        OwnedWorldLocationWriteResult.NoChange,
                        merged,
                        "Unchanged."));
                }

                if (current.StateRevisionId != expectedStateRevisionId ||
                    current.EnvironmentRevisionId != expectedEnvironmentRevisionId)
                {
                    return Task.FromResult(new OwnedWorldLocationWriteDecision(
                        OwnedWorldLocationWriteResult.Conflict,
                        current,
                        "Head changed."));
                }

                _locations[key] = desired;
                return Task.FromResult(new OwnedWorldLocationWriteDecision(
                    OwnedWorldLocationWriteResult.Updated,
                    desired,
                    "Updated."));
            }
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
        {
            lock (_gate)
            {
                return Task.FromResult(Snapshots.SingleOrDefault(snapshot =>
                    SnapshotKeyMatches(
                        snapshot,
                        ownerProvider,
                        ownerExternalId,
                        worldId,
                        installationId,
                        stateRevisionId,
                        environmentRevisionId)));
            }
        }

        public Task<IReadOnlyList<OwnedWorldSnapshot>> ListWorldSnapshotsAsync(
            string ownerProvider,
            string ownerExternalId,
            WorldId worldId,
            int maximumSnapshots,
            CancellationToken cancellationToken = default)
        {
            lock (_gate)
            {
                return Task.FromResult<IReadOnlyList<OwnedWorldSnapshot>>(
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
            }
        }

        public Task<OwnedWorldSnapshotWriteDecision> PublishAsync(
            OwnedWorldSnapshot snapshot,
            CancellationToken cancellationToken = default)
        {
            lock (_gate)
            {
                var existing = Snapshots.SingleOrDefault(candidate => SnapshotKeyMatches(
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
                    "Conflict."));
            }
        }

        private static bool SnapshotKeyMatches(
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

    private sealed class MemoryTransferStore : IPrivateSnapshotTransferStore
    {
        private readonly object _gate = new();
        public Dictionary<PrivateSnapshotTransferId, PrivateSnapshotTransferRecord> Records { get; } = [];

        public Task<bool> TryCreateAsync(
            PrivateSnapshotTransferRecord transfer,
            CancellationToken cancellationToken = default)
        {
            lock (_gate)
            {
                if (Records.Values.Any(existing =>
                    string.Equals(existing.ObjectKey, transfer.ObjectKey, StringComparison.Ordinal) &&
                    existing.State is PrivateSnapshotTransferState.Provisioning or
                        PrivateSnapshotTransferState.Active))
                {
                    return Task.FromResult(false);
                }

                Records[transfer.Id] = transfer;
                return Task.FromResult(true);
            }
        }

        public Task<PrivateSnapshotTransferRecord?> LoadAsync(
            PrivateSnapshotTransferId transferId,
            CancellationToken cancellationToken = default)
        {
            lock (_gate)
            {
                return Task.FromResult(Records.GetValueOrDefault(transferId));
            }
        }

        public Task<PrivateSnapshotTransferRecord?> LoadInFlightByObjectKeyAsync(
            string objectKey,
            CancellationToken cancellationToken = default)
        {
            lock (_gate)
            {
                return Task.FromResult(Records.Values.SingleOrDefault(record =>
                    string.Equals(record.ObjectKey, objectKey, StringComparison.Ordinal) &&
                    record.State is PrivateSnapshotTransferState.Provisioning or
                        PrivateSnapshotTransferState.Active));
            }
        }

        public Task<bool> TryActivateProvisioningAsync(
            PrivateSnapshotTransferId transferId,
            string ownerProvider,
            string ownerExternalId,
            string sourceInstallationId,
            string expectedPlaceholderProviderUploadId,
            string providerUploadId,
            CancellationToken cancellationToken = default)
        {
            lock (_gate)
            {
                if (!Records.TryGetValue(transferId, out var current) ||
                    current.State != PrivateSnapshotTransferState.Provisioning ||
                    !SameOwner(current, ownerProvider, ownerExternalId, sourceInstallationId) ||
                    !string.Equals(
                        current.ProviderUploadId,
                        expectedPlaceholderProviderUploadId,
                        StringComparison.Ordinal))
                {
                    return Task.FromResult(false);
                }

                Records[transferId] = current with
                {
                    ProviderUploadId = providerUploadId,
                    State = PrivateSnapshotTransferState.Active
                };
                return Task.FromResult(true);
            }
        }

        public Task<bool> TrySetStateAsync(
            PrivateSnapshotTransferId transferId,
            string ownerProvider,
            string ownerExternalId,
            string sourceInstallationId,
            PrivateSnapshotTransferState expectedState,
            PrivateSnapshotTransferState nextState,
            DateTimeOffset changedAt,
            CancellationToken cancellationToken = default)
        {
            lock (_gate)
            {
                if (!Records.TryGetValue(transferId, out var current) ||
                    current.State != expectedState ||
                    !SameOwner(current, ownerProvider, ownerExternalId, sourceInstallationId))
                {
                    return Task.FromResult(false);
                }

                Records[transferId] = current with
                {
                    State = nextState,
                    StateChangedAt = changedAt
                };
                return Task.FromResult(true);
            }
        }

        private static bool SameOwner(
            PrivateSnapshotTransferRecord record,
            string ownerProvider,
            string ownerExternalId,
            string sourceInstallationId)
            => string.Equals(record.OwnerProvider, ownerProvider, StringComparison.Ordinal) &&
               string.Equals(record.OwnerExternalId, ownerExternalId, StringComparison.Ordinal) &&
               string.Equals(record.SourceInstallationId, sourceInstallationId, StringComparison.Ordinal);
    }

    private sealed class DirectObjectStore : IPrivateImmutableObjectStore
    {
        private readonly Dictionary<string, UploadRecord> _uploads = new(StringComparer.Ordinal);
        private readonly Dictionary<string, byte[]> _objects = new(StringComparer.Ordinal);
        private int _nextUpload;

        public IReadOnlyDictionary<string, UploadRecord> Uploads => _uploads;
        public int DownloadAuthorizationCount { get; private set; }

        public Task<ImmutableUploadSession> BeginMultipartUploadAsync(
            string objectKey,
            long expectedByteSize,
            string expectedSha256,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var existing = _uploads.Values.SingleOrDefault(upload =>
                !upload.Completed &&
                string.Equals(upload.ObjectKey, objectKey, StringComparison.Ordinal));
            if (existing is not null)
            {
                return Task.FromResult(new ImmutableUploadSession(
                    existing.ProviderUploadId,
                    objectKey));
            }

            var providerId = $"private-upload-{++_nextUpload}";
            _uploads.Add(providerId, new UploadRecord(
                providerId,
                objectKey,
                expectedByteSize,
                expectedSha256));
            return Task.FromResult(new ImmutableUploadSession(providerId, objectKey));
        }

        public Task<ImmutableUploadSnapshot?> GetMultipartUploadAsync(
            string providerUploadId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!_uploads.TryGetValue(providerUploadId, out var upload))
            {
                return Task.FromResult<ImmutableUploadSnapshot?>(null);
            }

            return Task.FromResult<ImmutableUploadSnapshot?>(new(
                upload.ProviderUploadId,
                upload.ObjectKey,
                upload.Parts
                    .OrderBy(pair => pair.Key)
                    .Select(pair => new ImmutableUploadedPart(
                        pair.Key,
                        pair.Value.LongLength))
                    .ToArray(),
                upload.Completed));
        }

        public Task<DirectObjectTransferAuthorization> AuthorizeUploadPartAsync(
            string providerUploadId,
            int partNumber,
            long expectedByteSize,
            DateTimeOffset expiresAt,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!_uploads.ContainsKey(providerUploadId))
            {
                throw new InvalidOperationException("Unknown private upload.");
            }

            return Task.FromResult(new DirectObjectTransferAuthorization(
                new Uri($"https://object.test/private-upload/{providerUploadId}/{partNumber}"),
                "PUT",
                new Dictionary<string, string>(),
                expiresAt,
                expectedByteSize));
        }

        public Task<ImmutableStoredObject> CompleteMultipartUploadAsync(
            string providerUploadId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var upload = _uploads[providerUploadId];
            if (!upload.Completed)
            {
                using var stream = new MemoryStream();
                foreach (var part in upload.Parts.OrderBy(pair => pair.Key))
                {
                    stream.Write(part.Value);
                }

                _objects[upload.ObjectKey] = stream.ToArray();
                upload.Completed = true;
            }

            return Task.FromResult(Descriptor(upload.ObjectKey, _objects[upload.ObjectKey]));
        }

        public Task AbortMultipartUploadAsync(
            string providerUploadId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _uploads.Remove(providerUploadId);
            return Task.CompletedTask;
        }

        public Task<ImmutableStoredObject?> InspectObjectAsync(
            string objectKey,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(
                _objects.TryGetValue(objectKey, out var bytes)
                    ? Descriptor(objectKey, bytes)
                    : null);
        }

        public Task<DirectObjectTransferAuthorization> AuthorizeDownloadAsync(
            string objectKey,
            long expectedByteSize,
            DateTimeOffset expiresAt,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!_objects.ContainsKey(objectKey))
            {
                throw new InvalidOperationException("Unknown private snapshot object.");
            }

            DownloadAuthorizationCount++;
            return Task.FromResult(new DirectObjectTransferAuthorization(
                new Uri($"https://object.test/private-download/{Uri.EscapeDataString(objectKey)}"),
                "GET",
                new Dictionary<string, string>(),
                expiresAt,
                expectedByteSize));
        }

        public Task DeleteObjectAsync(
            string objectKey,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _objects.Remove(objectKey);
            return Task.CompletedTask;
        }

        public void UploadAll(string providerUploadId, byte[] bytes, int partSize)
        {
            var partNumber = 1;
            for (var offset = 0; offset < bytes.Length; offset += partSize)
            {
                var length = Math.Min(partSize, bytes.Length - offset);
                _uploads[providerUploadId].Parts[partNumber++] =
                    bytes[offset..(offset + length)];
            }
        }

        public void SeedObject(string objectKey, byte[] bytes)
            => _objects[objectKey] = bytes.ToArray();

        public void CorruptObject(string objectKey, byte[] bytes)
            => _objects[objectKey] = bytes.ToArray();

        private static ImmutableStoredObject Descriptor(string objectKey, byte[] bytes)
            => new(objectKey, bytes.LongLength, Hash(bytes));

        public sealed class UploadRecord
        {
            public UploadRecord(
                string providerUploadId,
                string objectKey,
                long expectedByteSize,
                string expectedSha256)
            {
                ProviderUploadId = providerUploadId;
                ObjectKey = objectKey;
                ExpectedByteSize = expectedByteSize;
                ExpectedSha256 = expectedSha256;
            }

            public string ProviderUploadId { get; }
            public string ObjectKey { get; }
            public long ExpectedByteSize { get; }
            public string ExpectedSha256 { get; }
            public SortedDictionary<int, byte[]> Parts { get; } = [];
            public bool Completed { get; set; }
        }
    }
}
