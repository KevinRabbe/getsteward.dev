using Npgsql;
using SharedWorlds.Backend.Identity;
using SharedWorlds.Backend.PostgreSql;
using SharedWorlds.Backend.Transfers;
using SharedWorlds.Backend.Worlds;
using SharedWorlds.Core.Domain;
using Xunit;

namespace SharedWorlds.Backend.PostgreSql.Tests;

public sealed class PostgreSqlSharedPackageProvisioningTests : IAsyncLifetime
{
    private static readonly DateTimeOffset Now =
        new(2026, 7, 22, 12, 0, 0, TimeSpan.Zero);

    private NpgsqlDataSource _dataSource = null!;
    private PostgreSqlSharedPackageTransferStore _transfers = null!;
    private VerifiedExternalIdentity _manager = null!;
    private SharedWorldMetadata _world = null!;

    public async Task InitializeAsync()
    {
        var connectionString = Environment.GetEnvironmentVariable("STEWARD_TEST_POSTGRES");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException(
                "STEWARD_TEST_POSTGRES must be set for PostgreSQL integration tests.");
        }

        _dataSource = NpgsqlDataSource.Create(connectionString);
        await PostgreSqlBackendSchema.InitializeAsync(_dataSource);
        await ResetAsync();

        var worldsStore = new PostgreSqlSharedWorldStore(_dataSource);
        var worlds = new SharedWorldMetadataService(worldsStore, () => Now);
        _manager = new VerifiedExternalIdentity(
            new ExternalIdentityRef("steam", "76561198000000001"));
        var created = await worlds.CreateSharedWorldAsync(
            _manager,
            new CreateSharedWorldCommand(
                WorldId.New(),
                "factorio",
                "Provisioning World",
                RevisionId.New(),
                null));
        _world = Assert.IsType<SharedWorldMetadata>(created.World);
        _transfers = new PostgreSqlSharedPackageTransferStore(_dataSource);
    }

    public async Task DisposeAsync() => await _dataSource.DisposeAsync();

    [Fact]
    public async Task OneInFlightTransferPerImmutableObjectKeyAndActivationIsCompareAndSet()
    {
        var first = ProvisioningTransfer("pending:first");
        var competing = ProvisioningTransfer("pending:second") with
        {
            Id = SharedPackageTransferId.New(),
            ProviderUploadId = "pending:second"
        };

        Assert.True(await _transfers.TryCreateAsync(first));
        Assert.False(await _transfers.TryCreateAsync(competing));

        var loaded = Assert.IsType<SharedPackageTransferRecord>(
            await _transfers.LoadInFlightByObjectKeyAsync(first.ObjectKey));
        Assert.Equal(first.Id, loaded.Id);
        Assert.Equal(SharedPackageTransferState.Provisioning, loaded.State);

        Assert.False(await _transfers.TryActivateProvisioningAsync(
            first.Id,
            new ExternalIdentityRef("steam", "76561198000000099"),
            first.ProviderUploadId,
            "provider-1"));
        Assert.False(await _transfers.TryActivateProvisioningAsync(
            first.Id,
            first.Owner,
            "pending:wrong",
            "provider-1"));

        Assert.True(await _transfers.TryActivateProvisioningAsync(
            first.Id,
            first.Owner,
            first.ProviderUploadId,
            "provider-1"));

        var active = Assert.IsType<SharedPackageTransferRecord>(
            await _transfers.LoadAsync(first.Id));
        Assert.Equal(SharedPackageTransferState.Active, active.State);
        Assert.Equal("provider-1", active.ProviderUploadId);
        Assert.False(await _transfers.TryActivateProvisioningAsync(
            first.Id,
            first.Owner,
            first.ProviderUploadId,
            "provider-2"));
        Assert.False(await _transfers.TryCreateAsync(competing));
    }

    [Fact]
    public async Task ObjectKeyCanBeReusedAfterPreviousTransferLeavesInFlightStates()
    {
        var first = ProvisioningTransfer("pending:first");
        Assert.True(await _transfers.TryCreateAsync(first));
        Assert.True(await _transfers.TryActivateProvisioningAsync(
            first.Id,
            first.Owner,
            first.ProviderUploadId,
            "provider-1"));
        Assert.True(await _transfers.TrySetStateAsync(
            first.Id,
            first.Owner,
            SharedPackageTransferState.Active,
            SharedPackageTransferState.Abandoned,
            Now));

        var replacement = ProvisioningTransfer("pending:replacement") with
        {
            Id = SharedPackageTransferId.New(),
            ProviderUploadId = "pending:replacement"
        };

        Assert.True(await _transfers.TryCreateAsync(replacement));
        var loaded = Assert.IsType<SharedPackageTransferRecord>(
            await _transfers.LoadInFlightByObjectKeyAsync(first.ObjectKey));
        Assert.Equal(replacement.Id, loaded.Id);
        Assert.Equal(SharedPackageTransferState.Provisioning, loaded.State);
    }

    [Fact]
    public async Task ProvisioningStateRoundTripsThroughCleanupQuery()
    {
        var transfer = ProvisioningTransfer("pending:cleanup") with
        {
            ExpiresAt = Now - TimeSpan.FromMinutes(1)
        };
        Assert.True(await _transfers.TryCreateAsync(transfer));

        var expired = await _transfers.ListByStateExpiringBeforeAsync(
            SharedPackageTransferState.Provisioning,
            Now,
            limit: 100);

        var loaded = Assert.Single(expired);
        Assert.Equal(transfer.Id, loaded.Id);
        Assert.Equal(SharedPackageTransferState.Provisioning, loaded.State);
    }

    private SharedPackageTransferRecord ProvisioningTransfer(string placeholder)
    {
        var revision = RevisionId.New();
        var objectKey = $"packages/{_world.WorldId}/state/{revision}/aaaaaaaa.package";
        return new SharedPackageTransferRecord(
            SharedPackageTransferId.New(),
            _world.WorldId,
            revision,
            SharedPackageKind.State,
            _world.AdapterId,
            _manager.Subject,
            objectKey,
            placeholder,
            4096,
            new string('A', 64),
            null,
            1024,
            4,
            Now,
            Now + TimeSpan.FromHours(24),
            SharedPackageTransferState.Provisioning);
    }

    private async Task ResetAsync()
    {
        await using var command = _dataSource.CreateCommand(
            "TRUNCATE TABLE steward_authority_idempotency, steward_object_cleanup_queue, " +
            "steward_world_reservations, steward_package_transfers, steward_world_invitations, " +
            "steward_state_revisions, steward_environment_revisions, steward_world_members, " +
            "steward_shared_worlds CASCADE;");
        await command.ExecuteNonQueryAsync();
    }
}
