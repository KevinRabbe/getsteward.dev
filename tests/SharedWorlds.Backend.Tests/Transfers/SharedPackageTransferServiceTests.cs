using System.Security.Cryptography;
using SharedWorlds.Backend.Identity;
using SharedWorlds.Backend.Transfers;
using SharedWorlds.Backend.Worlds;
using SharedWorlds.Core.Domain;
using Xunit;

namespace SharedWorlds.Backend.Tests.Transfers;

public sealed class SharedPackageTransferServiceTests
{
    private static readonly DateTimeOffset Start =
        new(2026, 7, 22, 2, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task BeginUploadRequiresActiveMembershipAndEnforcesHardRequestBounds()
    {
        var fixture = await Fixture.CreateAsync();
        var outsider = Steam("76561198000000099");
        var command = fixture.EnvironmentCommand([1, 2, 3, 4]);

        var unauthorized = await fixture.Transfers.BeginUploadAsync(outsider, command);
        var tooLarge = await fixture.Transfers.BeginUploadAsync(
            fixture.Manager,
            command with
            {
                ExpectedByteSize = SharedPackageTransferOptions.FirstReleaseMaximumPackageBytes + 1
            });
        var invalidHash = await fixture.Transfers.BeginUploadAsync(
            fixture.Manager,
            command with { ExpectedSha256 = "not-a-hash" });

        Assert.Equal(BeginPackageUploadStatus.NotFoundOrUnauthorized, unauthorized.Status);
        Assert.Equal(BeginPackageUploadStatus.InvalidRequest, tooLarge.Status);
        Assert.Equal(BeginPackageUploadStatus.InvalidRequest, invalidHash.Status);
        Assert.Empty(fixture.ObjectStore.Uploads);
    }

    [Fact]
    public async Task UploadPlanUsesExactPartLengthsAndRejectsOutOfRangeParts()
    {
        var options = new SharedPackageTransferOptions(
            maximumPackageBytes: 1024,
            partSizeBytes: 4,
            transferLifetime: TimeSpan.FromHours(24),
            authorizationLifetime: TimeSpan.FromMinutes(15));
        var fixture = await Fixture.CreateAsync(options);
        var bytes = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 };
        var begin = await fixture.Transfers.BeginUploadAsync(
            fixture.Manager,
            fixture.EnvironmentCommand(bytes));
        var transfer = Assert.IsType<SharedPackageTransferRecord>(begin.Transfer);

        var first = await fixture.Transfers.AuthorizePartAsync(fixture.Manager, transfer.Id, 1);
        var last = await fixture.Transfers.AuthorizePartAsync(fixture.Manager, transfer.Id, 3);
        var invalid = await fixture.Transfers.AuthorizePartAsync(fixture.Manager, transfer.Id, 4);

        Assert.Equal(3, transfer.PartCount);
        Assert.Equal(4, Assert.IsType<DirectObjectTransferAuthorization>(first.Authorization).ExpectedByteSize);
        Assert.Equal(2, Assert.IsType<DirectObjectTransferAuthorization>(last.Authorization).ExpectedByteSize);
        Assert.Equal(AuthorizePackagePartStatus.InvalidPart, invalid.Status);
    }

    [Fact]
    public async Task ProgressObservesBytesUploadedDirectlyToObjectStore()
    {
        var options = SmallParts();
        var fixture = await Fixture.CreateAsync(options);
        var bytes = new byte[] { 10, 20, 30, 40, 50, 60 };
        var begin = await fixture.Transfers.BeginUploadAsync(
            fixture.Manager,
            fixture.EnvironmentCommand(bytes));
        var transfer = Assert.IsType<SharedPackageTransferRecord>(begin.Transfer);
        await fixture.Transfers.AuthorizePartAsync(fixture.Manager, transfer.Id, 1);

        fixture.ObjectStore.UploadPart(transfer.ProviderUploadId, 1, bytes[..4]);
        var progress = Assert.IsType<SharedPackageTransferProgress>(
            await fixture.Transfers.GetProgressAsync(fixture.Manager, transfer.Id));

        var completed = Assert.Single(progress.CompletedParts);
        Assert.Equal(1, completed.PartNumber);
        Assert.Equal(4, completed.ByteSize);
        Assert.False(progress.ProviderUploadCompleted);
    }

    [Fact]
    public async Task FinalizeRejectsWrongBytesBeforeRevisionMetadataIsPublished()
    {
        var options = SmallParts();
        var fixture = await Fixture.CreateAsync(options);
        var expected = new byte[] { 1, 2, 3, 4 };
        var begin = await fixture.Transfers.BeginUploadAsync(
            fixture.Manager,
            fixture.EnvironmentCommand(expected));
        var transfer = Assert.IsType<SharedPackageTransferRecord>(begin.Transfer);

        fixture.ObjectStore.UploadPart(transfer.ProviderUploadId, 1, [9, 9, 9, 9]);
        var result = await fixture.Transfers.FinalizeAsync(fixture.Manager, transfer.Id);
        var persisted = Assert.IsType<SharedPackageTransferRecord>(
            await fixture.TransferStore.LoadAsync(transfer.Id));

        Assert.Equal(FinalizePackageUploadStatus.IntegrityMismatch, result.Status);
        Assert.Equal(SharedPackageTransferState.IntegrityFailed, persisted.State);
        Assert.Null(await fixture.RevisionStore.LoadEnvironmentRevisionAsync(
            transfer.WorldId,
            transfer.RevisionId));
    }

    [Fact]
    public async Task VerifiedEnvironmentPublishesMetadataWithoutAdvancingWorldHead()
    {
        var options = SmallParts();
        var fixture = await Fixture.CreateAsync(options);
        var originalWorld = fixture.World;
        var bytes = new byte[] { 1, 2, 3, 4, 5 };
        var command = fixture.EnvironmentCommand(bytes);
        var begin = await fixture.Transfers.BeginUploadAsync(fixture.Manager, command);
        var transfer = Assert.IsType<SharedPackageTransferRecord>(begin.Transfer);
        fixture.ObjectStore.UploadAll(transfer.ProviderUploadId, bytes, transfer.PartSizeBytes);

        var finalized = await fixture.Transfers.FinalizeAsync(fixture.Manager, transfer.Id);
        var metadata = Assert.IsType<SharedEnvironmentRevisionMetadata>(
            await fixture.RevisionStore.LoadEnvironmentRevisionAsync(
                command.WorldId,
                command.RevisionId));
        var world = Assert.IsType<SharedWorldMetadata>(
            await fixture.WorldStore.LoadWorldAsync(command.WorldId));

        Assert.Equal(FinalizePackageUploadStatus.Finalized, finalized.Status);
        Assert.Equal(transfer.ObjectKey, metadata.ArtifactReference);
        Assert.Equal(command.ExpectedByteSize, metadata.ByteSize);
        Assert.Equal(command.ExpectedSha256, metadata.Sha256);
        Assert.Equal(originalWorld.CurrentEnvironmentRevisionId, world.CurrentEnvironmentRevisionId);
        Assert.Equal(originalWorld.CurrentStateRevisionId, world.CurrentStateRevisionId);
    }

    [Fact]
    public async Task StateUploadRequiresRecordedEnvironmentBeforeBytesAreAuthorized()
    {
        var fixture = await Fixture.CreateAsync(SmallParts());
        var requiredEnvironment = RevisionId.New();
        var command = fixture.StateCommand([1, 2, 3], requiredEnvironment);

        var blocked = await fixture.Transfers.BeginUploadAsync(fixture.Manager, command);
        Assert.Equal(BeginPackageUploadStatus.RequiredEnvironmentMissing, blocked.Status);
        Assert.Empty(fixture.ObjectStore.Uploads);

        await fixture.Revisions.RecordVerifiedEnvironmentRevisionAsync(
            fixture.NativeEnvironment(requiredEnvironment));
        var allowed = await fixture.Transfers.BeginUploadAsync(fixture.Manager, command);

        Assert.Equal(BeginPackageUploadStatus.Started, allowed.Status);
        Assert.NotNull(allowed.Transfer);
    }

    [Fact]
    public async Task VerifiedStatePublishesPackageMetadataWithoutCanonicalCommit()
    {
        var fixture = await Fixture.CreateAsync(SmallParts());
        var environment = RevisionId.New();
        await fixture.Revisions.RecordVerifiedEnvironmentRevisionAsync(
            fixture.NativeEnvironment(environment));
        var bytes = new byte[] { 7, 8, 9, 10, 11 };
        var command = fixture.StateCommand(bytes, environment);
        var begin = await fixture.Transfers.BeginUploadAsync(fixture.Manager, command);
        var transfer = Assert.IsType<SharedPackageTransferRecord>(begin.Transfer);
        fixture.ObjectStore.UploadAll(transfer.ProviderUploadId, bytes, transfer.PartSizeBytes);

        var finalized = await fixture.Transfers.FinalizeAsync(fixture.Manager, transfer.Id);
        var metadata = Assert.IsType<SharedStateRevisionMetadata>(
            await fixture.RevisionStore.LoadStateRevisionAsync(command.WorldId, command.RevisionId));
        var world = Assert.IsType<SharedWorldMetadata>(
            await fixture.WorldStore.LoadWorldAsync(command.WorldId));

        Assert.Equal(FinalizePackageUploadStatus.Finalized, finalized.Status);
        Assert.Equal(transfer.ObjectKey, metadata.PackageObjectKey);
        Assert.Equal(environment, metadata.RequiredEnvironmentRevisionId);
        Assert.Equal(fixture.World.CurrentStateRevisionId, world.CurrentStateRevisionId);
    }

    [Fact]
    public async Task ExistingAuthorizedTransferCanFinishAfterMemberBecomesRevocationPending()
    {
        var fixture = await Fixture.CreateAsync(SmallParts());
        var bytes = new byte[] { 1, 2, 3, 4 };
        var command = fixture.EnvironmentCommand(bytes);
        var begin = await fixture.Transfers.BeginUploadAsync(fixture.Manager, command);
        var transfer = Assert.IsType<SharedPackageTransferRecord>(begin.Transfer);
        fixture.WorldStore.SetMemberStatus(
            fixture.World.WorldId,
            fixture.Manager.Subject,
            SharedWorldMemberStatus.RevocationPending);
        fixture.ObjectStore.UploadAll(transfer.ProviderUploadId, bytes, transfer.PartSizeBytes);

        var finalized = await fixture.Transfers.FinalizeAsync(fixture.Manager, transfer.Id);

        Assert.Equal(FinalizePackageUploadStatus.Finalized, finalized.Status);
        Assert.NotNull(await fixture.RevisionStore.LoadEnvironmentRevisionAsync(
            command.WorldId,
            command.RevisionId));
    }

    [Fact]
    public async Task TransferIdIsPrivateEvenFromAnotherActiveWorldMember()
    {
        var fixture = await Fixture.CreateAsync(SmallParts());
        var other = Steam("76561198000000002");
        fixture.WorldStore.AddMember(new SharedWorldMember(
            fixture.World.WorldId,
            other.Subject,
            SharedWorldMemberStatus.Active,
            Start));
        var begin = await fixture.Transfers.BeginUploadAsync(
            fixture.Manager,
            fixture.EnvironmentCommand([1, 2, 3, 4]));
        var transfer = Assert.IsType<SharedPackageTransferRecord>(begin.Transfer);

        Assert.Null(await fixture.Transfers.GetProgressAsync(other, transfer.Id));
        Assert.Equal(
            AuthorizePackagePartStatus.TransferNotFound,
            (await fixture.Transfers.AuthorizePartAsync(other, transfer.Id, 1)).Status);
        Assert.Equal(
            FinalizePackageUploadStatus.TransferNotFound,
            (await fixture.Transfers.FinalizeAsync(other, transfer.Id)).Status);
    }

    [Fact]
    public async Task DownloadAuthorizationRequiresActiveMembershipAndVerifiedStoredObject()
    {
        var fixture = await Fixture.CreateAsync(SmallParts());
        var bytes = new byte[] { 1, 3, 5, 7 };
        var command = fixture.EnvironmentCommand(bytes);
        var begin = await fixture.Transfers.BeginUploadAsync(fixture.Manager, command);
        var transfer = Assert.IsType<SharedPackageTransferRecord>(begin.Transfer);
        fixture.ObjectStore.UploadAll(transfer.ProviderUploadId, bytes, transfer.PartSizeBytes);
        await fixture.Transfers.FinalizeAsync(fixture.Manager, transfer.Id);
        var outsider = Steam("76561198000000099");

        var authorized = await fixture.Transfers.AuthorizeDownloadAsync(
            fixture.Manager,
            command.WorldId,
            command.RevisionId,
            SharedPackageKind.Environment);
        var hidden = await fixture.Transfers.AuthorizeDownloadAsync(
            outsider,
            command.WorldId,
            command.RevisionId,
            SharedPackageKind.Environment);

        Assert.Equal(AuthorizePackageDownloadStatus.Authorized, authorized.Status);
        Assert.Equal(command.ExpectedByteSize, authorized.ExpectedByteSize);
        Assert.Equal(command.ExpectedSha256, authorized.ExpectedSha256);
        Assert.NotNull(authorized.Authorization);
        Assert.Equal(AuthorizePackageDownloadStatus.NotFoundOrUnauthorized, hidden.Status);

        fixture.ObjectStore.CorruptObject(transfer.ObjectKey, [8, 8, 8, 8]);
        var integrityFailure = await fixture.Transfers.AuthorizeDownloadAsync(
            fixture.Manager,
            command.WorldId,
            command.RevisionId,
            SharedPackageKind.Environment);
        Assert.Equal(AuthorizePackageDownloadStatus.StorageIntegrityFailure, integrityFailure.Status);
    }

    [Fact]
    public async Task NativeEnvironmentReferenceDoesNotPretendThereIsAHostedDownload()
    {
        var fixture = await Fixture.CreateAsync();
        var revision = RevisionId.New();
        await fixture.Revisions.RecordVerifiedEnvironmentRevisionAsync(
            fixture.NativeEnvironment(revision));

        var result = await fixture.Transfers.AuthorizeDownloadAsync(
            fixture.Manager,
            fixture.World.WorldId,
            revision,
            SharedPackageKind.Environment);

        Assert.Equal(AuthorizePackageDownloadStatus.NoHostedPackage, result.Status);
        Assert.Null(result.Authorization);
    }

    [Fact]
    public async Task ExactPreexistingObjectPublishesMetadataWithoutStartingAnotherUpload()
    {
        var fixture = await Fixture.CreateAsync(SmallParts());
        var bytes = new byte[] { 4, 3, 2, 1 };
        var command = fixture.EnvironmentCommand(bytes);
        var objectKey = Fixture.ObjectKey(command);
        fixture.ObjectStore.SeedObject(objectKey, bytes);

        var result = await fixture.Transfers.BeginUploadAsync(fixture.Manager, command);

        Assert.Equal(BeginPackageUploadStatus.AlreadyPublished, result.Status);
        Assert.Null(result.Transfer);
        Assert.Empty(fixture.ObjectStore.Uploads);
        Assert.NotNull(await fixture.RevisionStore.LoadEnvironmentRevisionAsync(
            command.WorldId,
            command.RevisionId));
    }

    [Fact]
    public async Task ExpiredTransferCannotReceiveNewPartAuthorizationOrFinalize()
    {
        var fixture = await Fixture.CreateAsync(SmallParts());
        var begin = await fixture.Transfers.BeginUploadAsync(
            fixture.Manager,
            fixture.EnvironmentCommand([1, 2, 3, 4]));
        var transfer = Assert.IsType<SharedPackageTransferRecord>(begin.Transfer);
        fixture.Clock.Advance(TimeSpan.FromHours(25));

        Assert.Equal(
            AuthorizePackagePartStatus.Expired,
            (await fixture.Transfers.AuthorizePartAsync(fixture.Manager, transfer.Id, 1)).Status);
        Assert.Equal(
            FinalizePackageUploadStatus.Expired,
            (await fixture.Transfers.FinalizeAsync(fixture.Manager, transfer.Id)).Status);
    }

    [Fact]
    public async Task RetryAfterActiveBeginReturnsSameTransferWithoutAnotherProviderBegin()
    {
        var fixture = await Fixture.CreateAsync(SmallParts());
        var command = fixture.EnvironmentCommand([1, 2, 3, 4]);

        var first = await fixture.Transfers.BeginUploadAsync(fixture.Manager, command);
        var firstTransfer = Assert.IsType<SharedPackageTransferRecord>(first.Transfer);
        var beginCalls = fixture.ObjectStore.BeginCallCount;

        var retry = await fixture.Transfers.BeginUploadAsync(fixture.Manager, command);
        var retriedTransfer = Assert.IsType<SharedPackageTransferRecord>(retry.Transfer);

        Assert.Equal(BeginPackageUploadStatus.Started, retry.Status);
        Assert.Equal(firstTransfer, retriedTransfer);
        Assert.Equal(beginCalls, fixture.ObjectStore.BeginCallCount);
        Assert.Single(fixture.ObjectStore.Uploads);
    }

    [Fact]
    public async Task RetryAfterProviderBeginFailureResumesSameProvisioningTransferAndProviderUpload()
    {
        var fixture = await Fixture.CreateAsync(SmallParts());
        var command = fixture.EnvironmentCommand([1, 2, 3, 4]);
        fixture.ObjectStore.ThrowAfterCreatingNextUpload = true;

        await Assert.ThrowsAsync<IOException>(
            () => fixture.Transfers.BeginUploadAsync(fixture.Manager, command));

        var provisioning = Assert.Single(fixture.TransferStore.Records);
        Assert.Equal(SharedPackageTransferState.Provisioning, provisioning.State);
        Assert.StartsWith("pending:", provisioning.ProviderUploadId, StringComparison.Ordinal);
        var originalProviderUpload = Assert.Single(fixture.ObjectStore.Uploads).Key;

        var retry = await fixture.Transfers.BeginUploadAsync(fixture.Manager, command);
        var active = Assert.IsType<SharedPackageTransferRecord>(retry.Transfer);

        Assert.Equal(BeginPackageUploadStatus.Started, retry.Status);
        Assert.Equal(provisioning.Id, active.Id);
        Assert.Equal(SharedPackageTransferState.Active, active.State);
        Assert.Equal(originalProviderUpload, active.ProviderUploadId);
        Assert.Single(fixture.ObjectStore.Uploads);
        Assert.Equal(2, fixture.ObjectStore.BeginCallCount);
    }

    [Fact]
    public async Task ConflictingCallerCannotTakeOverExistingInFlightTransfer()
    {
        var fixture = await Fixture.CreateAsync(SmallParts());
        var other = Steam("76561198000000002");
        fixture.WorldStore.AddMember(new SharedWorldMember(
            fixture.World.WorldId,
            other.Subject,
            SharedWorldMemberStatus.Active,
            Start));
        var command = fixture.EnvironmentCommand([1, 2, 3, 4]);
        var first = await fixture.Transfers.BeginUploadAsync(fixture.Manager, command);
        Assert.NotNull(first.Transfer);

        var conflict = await fixture.Transfers.BeginUploadAsync(other, command);

        Assert.Equal(BeginPackageUploadStatus.Conflict, conflict.Status);
        Assert.Null(conflict.Transfer);
        Assert.Single(fixture.ObjectStore.Uploads);
    }

    private static SharedPackageTransferOptions SmallParts()
        => new(
            maximumPackageBytes: 1024,
            partSizeBytes: 4,
            transferLifetime: TimeSpan.FromHours(24),
            authorizationLifetime: TimeSpan.FromMinutes(15));

    private static VerifiedExternalIdentity Steam(string id)
        => new(new ExternalIdentityRef("steam", id));

    private sealed class Fixture
    {
        private Fixture(
            TestClock clock,
            BackendStore backendStore,
            TransferStore transferStore,
            DirectObjectStore objectStore,
            SharedRevisionMetadataService revisions,
            SharedPackageTransferService transfers,
            VerifiedExternalIdentity manager,
            SharedWorldMetadata world)
        {
            Clock = clock;
            WorldStore = backendStore;
            RevisionStore = backendStore;
            TransferStore = transferStore;
            ObjectStore = objectStore;
            Revisions = revisions;
            Transfers = transfers;
            Manager = manager;
            World = world;
        }

        public TestClock Clock { get; }
        public BackendStore WorldStore { get; }
        public BackendStore RevisionStore { get; }
        public TransferStore TransferStore { get; }
        public DirectObjectStore ObjectStore { get; }
        public SharedRevisionMetadataService Revisions { get; }
        public SharedPackageTransferService Transfers { get; }
        public VerifiedExternalIdentity Manager { get; }
        public SharedWorldMetadata World { get; }

        public static async Task<Fixture> CreateAsync(SharedPackageTransferOptions? options = null)
        {
            var clock = new TestClock();
            var backendStore = new BackendStore();
            var transferStore = new TransferStore();
            var objectStore = new DirectObjectStore(clock);
            var metadata = new SharedWorldMetadataService(backendStore, () => clock.Now);
            var revisions = new SharedRevisionMetadataService(backendStore, backendStore);
            var manager = Steam("76561198000000001");
            var created = await metadata.CreateSharedWorldAsync(
                manager,
                new CreateSharedWorldCommand(
                    WorldId.New(),
                    "factorio",
                    "Factory World",
                    RevisionId.New(),
                    RevisionId.New()));
            var world = Assert.IsType<SharedWorldMetadata>(created.World);
            var nextTransfer = 0;
            var transfers = new SharedPackageTransferService(
                backendStore,
                revisions,
                transferStore,
                objectStore,
                () => clock.Now,
                options,
                () => new SharedPackageTransferId(new Guid(++nextTransfer, 0, 0, new byte[8])));
            return new Fixture(
                clock,
                backendStore,
                transferStore,
                objectStore,
                revisions,
                transfers,
                manager,
                world);
        }

        public BeginPackageUploadCommand EnvironmentCommand(byte[] bytes)
            => new(
                World.WorldId,
                RevisionId.New(),
                SharedPackageKind.Environment,
                bytes.LongLength,
                Hash(bytes));

        public BeginPackageUploadCommand StateCommand(byte[] bytes, RevisionId? environment)
            => new(
                World.WorldId,
                RevisionId.New(),
                SharedPackageKind.State,
                bytes.LongLength,
                Hash(bytes),
                environment);

        public SharedEnvironmentRevisionMetadata NativeEnvironment(RevisionId revisionId)
            => new(
                World.WorldId,
                revisionId,
                World.AdapterId,
                "steam-manifest:factorio:stable",
                null,
                null,
                Manager.Subject,
                Clock.Now);

        public static string ObjectKey(BeginPackageUploadCommand command)
        {
            var kind = command.Kind == SharedPackageKind.State ? "state" : "environment";
            return $"packages/{command.WorldId}/{kind}/{command.RevisionId}/{command.ExpectedSha256.ToLowerInvariant()}.package";
        }

        private static string Hash(byte[] bytes)
            => Convert.ToHexString(SHA256.HashData(bytes));
    }

    private sealed class TestClock
    {
        public DateTimeOffset Now { get; private set; } = Start;
        public void Advance(TimeSpan duration) => Now += duration;
    }

    private sealed class TransferStore : ISharedPackageTransferStore
    {
        private readonly object _gate = new();
        private readonly Dictionary<SharedPackageTransferId, SharedPackageTransferRecord> _records = [];

        public IReadOnlyCollection<SharedPackageTransferRecord> Records
        {
            get
            {
                lock (_gate)
                {
                    return _records.Values.ToArray();
                }
            }
        }

        public Task<bool> TryCreateAsync(
            SharedPackageTransferRecord transfer,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_gate)
            {
                if (_records.ContainsKey(transfer.Id) ||
                    _records.Values.Any(existing =>
                        string.Equals(existing.ObjectKey, transfer.ObjectKey, StringComparison.Ordinal) &&
                        existing.State is SharedPackageTransferState.Active or SharedPackageTransferState.Provisioning))
                {
                    return Task.FromResult(false);
                }

                _records.Add(transfer.Id, transfer);
                return Task.FromResult(true);
            }
        }

        public Task<SharedPackageTransferRecord?> LoadAsync(
            SharedPackageTransferId transferId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_gate)
            {
                _records.TryGetValue(transferId, out var transfer);
                return Task.FromResult(transfer);
            }
        }

        public Task<SharedPackageTransferRecord?> LoadInFlightByObjectKeyAsync(
            string objectKey,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_gate)
            {
                return Task.FromResult(
                    _records.Values.SingleOrDefault(existing =>
                        string.Equals(existing.ObjectKey, objectKey, StringComparison.Ordinal) &&
                        existing.State is SharedPackageTransferState.Active or SharedPackageTransferState.Provisioning));
            }
        }

        public Task<bool> TryActivateProvisioningAsync(
            SharedPackageTransferId transferId,
            ExternalIdentityRef expectedOwner,
            string expectedPlaceholderProviderUploadId,
            string providerUploadId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_gate)
            {
                if (!_records.TryGetValue(transferId, out var transfer) ||
                    transfer.Owner != expectedOwner ||
                    transfer.State != SharedPackageTransferState.Provisioning ||
                    !string.Equals(
                        transfer.ProviderUploadId,
                        expectedPlaceholderProviderUploadId,
                        StringComparison.Ordinal))
                {
                    return Task.FromResult(false);
                }

                _records[transferId] = transfer with
                {
                    ProviderUploadId = providerUploadId,
                    State = SharedPackageTransferState.Active
                };
                return Task.FromResult(true);
            }
        }

        public Task<bool> TrySetStateAsync(
            SharedPackageTransferId transferId,
            ExternalIdentityRef expectedOwner,
            SharedPackageTransferState expectedState,
            SharedPackageTransferState nextState,
            DateTimeOffset changedAt,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_gate)
            {
                if (!_records.TryGetValue(transferId, out var transfer) ||
                    transfer.Owner != expectedOwner ||
                    transfer.State != expectedState)
                {
                    return Task.FromResult(false);
                }

                _records[transferId] = transfer with
                {
                    State = nextState,
                    FinalizedAt = nextState == SharedPackageTransferState.Finalized
                        ? changedAt
                        : transfer.FinalizedAt
                };
                return Task.FromResult(true);
            }
        }
    }

    private sealed class BackendStore : ISharedWorldMetadataStore, ISharedRevisionMetadataStore
    {
        private readonly object _gate = new();
        private readonly Dictionary<WorldId, SharedWorldMetadata> _worlds = [];
        private readonly Dictionary<(WorldId, ExternalIdentityRef), SharedWorldMember> _members = [];
        private readonly Dictionary<(WorldId, RevisionId), SharedStateRevisionMetadata> _states = [];
        private readonly Dictionary<(WorldId, RevisionId), SharedEnvironmentRevisionMetadata> _environments = [];

        public Task<bool> TryCreateWorldWithManagerAsync(
            SharedWorldMetadata world,
            SharedWorldMember accessManager,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_gate)
            {
                if (!_worlds.TryAdd(world.WorldId, world))
                {
                    return Task.FromResult(false);
                }

                _members.Add((world.WorldId, accessManager.Identity), accessManager);
                return Task.FromResult(true);
            }
        }

        public Task<SharedWorldMetadata?> LoadWorldAsync(
            WorldId worldId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_gate)
            {
                _worlds.TryGetValue(worldId, out var world);
                return Task.FromResult(world);
            }
        }

        public Task<SharedWorldMember?> LoadMemberAsync(
            WorldId worldId,
            ExternalIdentityRef identity,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_gate)
            {
                _members.TryGetValue((worldId, identity), out var member);
                return Task.FromResult(member);
            }
        }

        public Task<IReadOnlyList<SharedWorldMetadata>> ListWorldsForActiveMemberAsync(
            ExternalIdentityRef identity,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_gate)
            {
                return Task.FromResult<IReadOnlyList<SharedWorldMetadata>>(
                    _members.Values
                        .Where(member => member.Identity == identity &&
                                         member.Status == SharedWorldMemberStatus.Active)
                        .Select(member => _worlds[member.WorldId])
                        .ToArray());
            }
        }

        public Task<StoreRevisionMetadataStatus> TryRecordStateRevisionAsync(
            SharedStateRevisionMetadata revision,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_gate)
            {
                var key = (revision.WorldId, revision.RevisionId);
                if (_states.TryGetValue(key, out var existing))
                {
                    return Task.FromResult(existing == revision
                        ? StoreRevisionMetadataStatus.AlreadyRecorded
                        : StoreRevisionMetadataStatus.Conflict);
                }

                _states.Add(key, revision);
                return Task.FromResult(StoreRevisionMetadataStatus.Recorded);
            }
        }

        public Task<StoreRevisionMetadataStatus> TryRecordEnvironmentRevisionAsync(
            SharedEnvironmentRevisionMetadata revision,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_gate)
            {
                var key = (revision.WorldId, revision.RevisionId);
                if (_environments.TryGetValue(key, out var existing))
                {
                    return Task.FromResult(existing == revision
                        ? StoreRevisionMetadataStatus.AlreadyRecorded
                        : StoreRevisionMetadataStatus.Conflict);
                }

                _environments.Add(key, revision);
                return Task.FromResult(StoreRevisionMetadataStatus.Recorded);
            }
        }

        public Task<SharedStateRevisionMetadata?> LoadStateRevisionAsync(
            WorldId worldId,
            RevisionId revisionId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_gate)
            {
                _states.TryGetValue((worldId, revisionId), out var revision);
                return Task.FromResult(revision);
            }
        }

        public Task<SharedEnvironmentRevisionMetadata?> LoadEnvironmentRevisionAsync(
            WorldId worldId,
            RevisionId revisionId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_gate)
            {
                _environments.TryGetValue((worldId, revisionId), out var revision);
                return Task.FromResult(revision);
            }
        }

        public void AddMember(SharedWorldMember member)
        {
            lock (_gate)
            {
                _members[(member.WorldId, member.Identity)] = member;
            }
        }

        public void SetMemberStatus(
            WorldId worldId,
            ExternalIdentityRef identity,
            SharedWorldMemberStatus status)
        {
            lock (_gate)
            {
                var member = _members[(worldId, identity)];
                _members[(worldId, identity)] = member with { Status = status };
            }
        }
    }

    private sealed class DirectObjectStore : IPrivateImmutableObjectStore
    {
        private readonly TestClock _clock;
        private readonly Dictionary<string, UploadRecord> _uploads = new(StringComparer.Ordinal);
        private readonly Dictionary<string, byte[]> _objects = new(StringComparer.Ordinal);
        private int _nextUpload;

        public DirectObjectStore(TestClock clock)
        {
            _clock = clock;
        }

        public IReadOnlyDictionary<string, UploadRecord> Uploads => _uploads;
        public int BeginCallCount { get; private set; }
        public bool ThrowAfterCreatingNextUpload { get; set; }

        public Task<ImmutableUploadSession> BeginMultipartUploadAsync(
            string objectKey,
            long expectedByteSize,
            string expectedSha256,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            BeginCallCount++;

            var existing = _uploads.Values.SingleOrDefault(upload =>
                !upload.Completed &&
                string.Equals(upload.ObjectKey, objectKey, StringComparison.Ordinal));
            if (existing is not null)
            {
                return Task.FromResult(new ImmutableUploadSession(existing.ProviderUploadId, objectKey));
            }

            var providerId = $"upload-{++_nextUpload}";
            _uploads.Add(providerId, new UploadRecord(
                providerId,
                objectKey,
                expectedByteSize,
                expectedSha256));

            if (ThrowAfterCreatingNextUpload)
            {
                ThrowAfterCreatingNextUpload = false;
                throw new IOException("Simulated provider success followed by lost backend response.");
            }

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
                    .Select(pair => new ImmutableUploadedPart(pair.Key, pair.Value.LongLength))
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
                throw new InvalidOperationException("Unknown provider upload.");
            }

            return Task.FromResult(new DirectObjectTransferAuthorization(
                new Uri($"https://object.test/upload/{providerUploadId}/{partNumber}"),
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
                throw new InvalidOperationException("Unknown object.");
            }

            return Task.FromResult(new DirectObjectTransferAuthorization(
                new Uri($"https://object.test/download/{Uri.EscapeDataString(objectKey)}"),
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

        public void UploadPart(string providerUploadId, int partNumber, byte[] bytes)
            => _uploads[providerUploadId].Parts[partNumber] = bytes.ToArray();

        public void UploadAll(string providerUploadId, byte[] bytes, int partSize)
        {
            var partNumber = 1;
            for (var offset = 0; offset < bytes.Length; offset += partSize)
            {
                var length = Math.Min(partSize, bytes.Length - offset);
                UploadPart(providerUploadId, partNumber++, bytes[offset..(offset + length)]);
            }
        }

        public void SeedObject(string objectKey, byte[] bytes)
            => _objects[objectKey] = bytes.ToArray();

        public void CorruptObject(string objectKey, byte[] bytes)
            => _objects[objectKey] = bytes.ToArray();

        private static ImmutableStoredObject Descriptor(string objectKey, byte[] bytes)
            => new(objectKey, bytes.LongLength, Convert.ToHexString(SHA256.HashData(bytes)));

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
