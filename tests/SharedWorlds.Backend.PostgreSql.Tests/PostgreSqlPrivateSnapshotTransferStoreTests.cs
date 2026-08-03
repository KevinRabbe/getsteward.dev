using Npgsql;
using SharedWorlds.Backend.PostgreSql;
using SharedWorlds.Backend.Transfers;
using SharedWorlds.Core.Domain;
using SharedWorlds.Core.Environment;
using SharedWorlds.Core.Worlds;
using Xunit;

namespace SharedWorlds.Backend.PostgreSql.Tests;

public sealed class PostgreSqlPrivateSnapshotTransferStoreTests
{
    private const string PackageSha256 =
        "0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF";

    [Fact]
    public async Task ProvisioningRoundTripsAndCanResumeAfterStoreRecreation()
    {
        await WithStoresAsync(async (dataSource, locationStore, transferStore, owner, installationId) =>
        {
            var head = await PublishLocationAsync(locationStore, owner, installationId);
            var transfer = Transfer(owner, installationId, head);

            Assert.True(await transferStore.TryCreateAsync(transfer));
            var recreated = new PostgreSqlPrivateSnapshotTransferStore(dataSource);
            var loaded = Assert.IsType<PrivateSnapshotTransferRecord>(
                await recreated.LoadAsync(transfer.Id));
            var byObject = Assert.IsType<PrivateSnapshotTransferRecord>(
                await recreated.LoadInFlightByObjectKeyAsync(transfer.ObjectKey));

            Assert.Equal(transfer.Id, loaded.Id);
            Assert.Equal(transfer.ObjectKey, loaded.ObjectKey);
            Assert.Equal(transfer.ExpectedSha256, loaded.ExpectedSha256);
            Assert.Equal(transfer.EnvironmentManifest.AdapterId, loaded.EnvironmentManifest.AdapterId);
            Assert.Equal(PrivateSnapshotTransferState.Provisioning, loaded.State);
            Assert.Equal(loaded.Id, byObject.Id);
        });
    }

    [Fact]
    public async Task ConcurrentObjectKeyCreationHasExactlyOneWinner()
    {
        await WithStoresAsync(async (_, locationStore, transferStore, owner, installationId) =>
        {
            var head = await PublishLocationAsync(locationStore, owner, installationId);
            var first = Transfer(owner, installationId, head);
            var second = first with
            {
                Id = PrivateSnapshotTransferId.New(),
                ProviderUploadId = string.Empty
            };
            second = second with { ProviderUploadId = $"pending:{second.Id.Value:N}" };

            var results = await Task.WhenAll(
                transferStore.TryCreateAsync(first),
                transferStore.TryCreateAsync(second));

            Assert.Single(results, result => result);
            var loaded = Assert.IsType<PrivateSnapshotTransferRecord>(
                await transferStore.LoadInFlightByObjectKeyAsync(first.ObjectKey));
            Assert.Contains(loaded.Id, new[] { first.Id, second.Id });
        });
    }

    [Fact]
    public async Task ActivationRequiresExactOwnerInstallationStateAndPlaceholder()
    {
        await WithStoresAsync(async (_, locationStore, transferStore, owner, installationId) =>
        {
            var head = await PublishLocationAsync(locationStore, owner, installationId);
            var transfer = Transfer(owner, installationId, head);
            Assert.True(await transferStore.TryCreateAsync(transfer));

            Assert.False(await transferStore.TryActivateProvisioningAsync(
                transfer.Id,
                owner.Provider,
                owner.ExternalId,
                "wrong-installation",
                transfer.ProviderUploadId,
                "provider-1"));
            Assert.False(await transferStore.TryActivateProvisioningAsync(
                transfer.Id,
                owner.Provider,
                owner.ExternalId,
                installationId,
                "wrong-placeholder",
                "provider-1"));
            Assert.True(await transferStore.TryActivateProvisioningAsync(
                transfer.Id,
                owner.Provider,
                owner.ExternalId,
                installationId,
                transfer.ProviderUploadId,
                "provider-1"));
            Assert.False(await transferStore.TryActivateProvisioningAsync(
                transfer.Id,
                owner.Provider,
                owner.ExternalId,
                installationId,
                transfer.ProviderUploadId,
                "provider-2"));

            var loaded = Assert.IsType<PrivateSnapshotTransferRecord>(
                await transferStore.LoadAsync(transfer.Id));
            Assert.Equal(PrivateSnapshotTransferState.Active, loaded.State);
            Assert.Equal("provider-1", loaded.ProviderUploadId);
            Assert.Null(loaded.StateChangedAt);
        });
    }

    [Fact]
    public async Task TerminalStateTransitionIsExactCompareAndSwap()
    {
        await WithStoresAsync(async (_, locationStore, transferStore, owner, installationId) =>
        {
            var head = await PublishLocationAsync(locationStore, owner, installationId);
            var transfer = Transfer(owner, installationId, head);
            var providerUploadId = $"provider-{transfer.Id.Value:N}";
            Assert.True(await transferStore.TryCreateAsync(transfer));
            Assert.True(await transferStore.TryActivateProvisioningAsync(
                transfer.Id,
                owner.Provider,
                owner.ExternalId,
                installationId,
                transfer.ProviderUploadId,
                providerUploadId));
            var changedAt = DateTimeOffset.UtcNow;

            Assert.False(await transferStore.TrySetStateAsync(
                transfer.Id,
                owner.Provider,
                owner.ExternalId,
                "wrong-installation",
                PrivateSnapshotTransferState.Active,
                PrivateSnapshotTransferState.Finalized,
                changedAt));
            Assert.True(await transferStore.TrySetStateAsync(
                transfer.Id,
                owner.Provider,
                owner.ExternalId,
                installationId,
                PrivateSnapshotTransferState.Active,
                PrivateSnapshotTransferState.Finalized,
                changedAt));
            Assert.False(await transferStore.TrySetStateAsync(
                transfer.Id,
                owner.Provider,
                owner.ExternalId,
                installationId,
                PrivateSnapshotTransferState.Active,
                PrivateSnapshotTransferState.IntegrityFailed,
                changedAt.AddSeconds(1)));

            var loaded = Assert.IsType<PrivateSnapshotTransferRecord>(
                await transferStore.LoadAsync(transfer.Id));
            Assert.Equal(PrivateSnapshotTransferState.Finalized, loaded.State);
            Assert.NotNull(loaded.StateChangedAt);
        });
    }

    [Fact]
    public async Task ProviderUploadIdCannotBeAttachedToTwoTransfers()
    {
        await WithStoresAsync(async (_, locationStore, transferStore, owner, installationId) =>
        {
            var firstHead = await PublishLocationAsync(locationStore, owner, installationId);
            var first = Transfer(owner, installationId, firstHead);
            Assert.True(await transferStore.TryCreateAsync(first));
            Assert.True(await transferStore.TryActivateProvisioningAsync(
                first.Id,
                owner.Provider,
                owner.ExternalId,
                installationId,
                first.ProviderUploadId,
                "provider-shared"));

            var secondHead = await AdvanceLocationAsync(
                locationStore,
                owner,
                installationId,
                firstHead);
            var second = Transfer(owner, installationId, secondHead);
            Assert.True(await transferStore.TryCreateAsync(second));

            Assert.False(await transferStore.TryActivateProvisioningAsync(
                second.Id,
                owner.Provider,
                owner.ExternalId,
                installationId,
                second.ProviderUploadId,
                "provider-shared"));
            var loaded = Assert.IsType<PrivateSnapshotTransferRecord>(
                await transferStore.LoadAsync(second.Id));
            Assert.Equal(PrivateSnapshotTransferState.Provisioning, loaded.State);
        });
    }

    [Fact]
    public async Task ExactHistoricalLocationHeadIsRequiredByForeignKey()
    {
        await WithStoresAsync(async (_, _, transferStore, owner, installationId) =>
        {
            var unrecordedHead = new LocationHead(
                WorldId.New(),
                RevisionId.New(),
                RevisionId.New());
            var transfer = Transfer(owner, installationId, unrecordedHead);

            var exception = await Assert.ThrowsAsync<PostgresException>(() =>
                transferStore.TryCreateAsync(transfer));
            Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, exception.SqlState);
        });
    }

    [Fact]
    public async Task MalformedPersistedManifestFailsClosedOnRead()
    {
        await WithStoresAsync(async (dataSource, locationStore, transferStore, owner, installationId) =>
        {
            var head = await PublishLocationAsync(locationStore, owner, installationId);
            var transfer = Transfer(owner, installationId, head);
            Assert.True(await transferStore.TryCreateAsync(transfer));

            await using (var command = dataSource.CreateCommand("""
                UPDATE steward_private_snapshot_transfers
                   SET environment_manifest_json = '{}'::jsonb
                 WHERE transfer_id = @transfer_id;
                """))
            {
                command.Parameters.AddWithValue("transfer_id", transfer.Id.Value);
                await command.ExecuteNonQueryAsync();
            }

            await Assert.ThrowsAsync<InvalidDataException>(() =>
                transferStore.LoadAsync(transfer.Id));
        });
    }

    [Fact]
    public async Task InvalidNewTransferStateIsRejectedBeforeDatabaseMutation()
    {
        await WithStoresAsync(async (_, locationStore, transferStore, owner, installationId) =>
        {
            var head = await PublishLocationAsync(locationStore, owner, installationId);
            var transfer = Transfer(owner, installationId, head) with
            {
                State = PrivateSnapshotTransferState.Active,
                ProviderUploadId = "provider-without-provisioning"
            };

            await Assert.ThrowsAsync<InvalidDataException>(() =>
                transferStore.TryCreateAsync(transfer));
            Assert.Null(await transferStore.LoadAsync(transfer.Id));
        });
    }

    private static async Task WithStoresAsync(
        Func<NpgsqlDataSource,
            PostgreSqlOwnedWorldLocationStore,
            PostgreSqlPrivateSnapshotTransferStore,
            UserIdentity,
            string,
            Task> test)
    {
        var connectionString = Environment.GetEnvironmentVariable("STEWARD_TEST_POSTGRES");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return;
        }

        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        var locationStore = new PostgreSqlOwnedWorldLocationStore(dataSource);
        await locationStore.InitializeAsync();
        var snapshotStore = new PostgreSqlOwnedWorldSnapshotStore(dataSource);
        await snapshotStore.InitializeAsync();
        var transferStore = new PostgreSqlPrivateSnapshotTransferStore(dataSource);
        await transferStore.InitializeAsync();
        var suffix = Guid.NewGuid().ToString("N");
        var owner = new UserIdentity("steam", $"owner-{suffix}", "Owner");
        var installationId = $"pc-{suffix}";
        var registry = new OwnedWorldLocationRegistry(locationStore);
        await registry.RegisterInstallationAsync(
            owner,
            installationId,
            installationId,
            DateTimeOffset.UtcNow);
        await test(dataSource, locationStore, transferStore, owner, installationId);
    }

    private static async Task<LocationHead> PublishLocationAsync(
        PostgreSqlOwnedWorldLocationStore store,
        UserIdentity owner,
        string installationId)
    {
        var head = new LocationHead(
            WorldId.New(),
            RevisionId.New(),
            RevisionId.New());
        var registry = new OwnedWorldLocationRegistry(store);
        var result = await registry.PublishLocationWithPresentationAsync(
            owner,
            installationId,
            head.WorldId,
            head.StateRevisionId,
            head.EnvironmentRevisionId,
            "Factory World",
            "factorio",
            DateTimeOffset.UtcNow);
        Assert.Equal(OwnedWorldLocationWriteResult.Created, result.Result);
        return head;
    }

    private static async Task<LocationHead> AdvanceLocationAsync(
        PostgreSqlOwnedWorldLocationStore store,
        UserIdentity owner,
        string installationId,
        LocationHead current)
    {
        var next = new LocationHead(
            current.WorldId,
            RevisionId.New(),
            RevisionId.New());
        var registry = new OwnedWorldLocationRegistry(store);
        var result = await registry.PublishLocationWithPresentationAsync(
            owner,
            installationId,
            next.WorldId,
            next.StateRevisionId,
            next.EnvironmentRevisionId,
            "Factory World",
            "factorio",
            DateTimeOffset.UtcNow.AddMinutes(1),
            expectedStateRevisionId: current.StateRevisionId,
            expectedEnvironmentRevisionId: current.EnvironmentRevisionId);
        Assert.Equal(OwnedWorldLocationWriteResult.Updated, result.Result);
        return next;
    }

    private static PrivateSnapshotTransferRecord Transfer(
        UserIdentity owner,
        string installationId,
        LocationHead head)
    {
        var id = PrivateSnapshotTransferId.New();
        var createdAt = TruncateToMicroseconds(DateTimeOffset.UtcNow);
        return new PrivateSnapshotTransferRecord(
            id,
            owner.Provider,
            owner.ExternalId,
            installationId,
            head.WorldId,
            head.StateRevisionId,
            head.EnvironmentRevisionId,
            "factorio",
            $"private-snapshots/{Guid.NewGuid():N}.package",
            $"pending:{id.Value:N}",
            ExpectedByteSize: 10,
            PackageSha256,
            new EnvironmentManifest(
                SchemaVersion: 1,
                AdapterId: "factorio",
                GameVersion: "2.0.0",
                Components: [],
                Configuration: new Dictionary<string, string>()),
            PartSizeBytes: 4,
            PartCount: 3,
            createdAt,
            createdAt.AddHours(24),
            PrivateSnapshotTransferState.Provisioning);
    }

    private static DateTimeOffset TruncateToMicroseconds(DateTimeOffset value)
        => new(value.Ticks - (value.Ticks % 10), value.Offset);

    private sealed record LocationHead(
        WorldId WorldId,
        RevisionId StateRevisionId,
        RevisionId EnvironmentRevisionId);
}
