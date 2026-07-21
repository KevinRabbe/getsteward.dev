using Npgsql;
using SharedWorlds.Backend.Identity;
using SharedWorlds.Backend.PostgreSql;
using SharedWorlds.Backend.Transfers;
using SharedWorlds.Backend.Worlds;
using SharedWorlds.Core.Domain;
using Xunit;

namespace SharedWorlds.Backend.PostgreSql.Tests;

public sealed class PostgreSqlSharedPackageTransferStoreTests : IAsyncLifetime
{
    private const string HashA = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";

    private static readonly DateTimeOffset Now =
        new(2026, 7, 22, 2, 30, 0, TimeSpan.Zero);

    private NpgsqlDataSource _dataSource = null!;
    private PostgreSqlSharedPackageTransferStore _transfers = null!;
    private PostgreSqlSharedWorldStore _worldStore = null!;
    private SharedWorldMetadataService _worlds = null!;
    private SharedRevisionMetadataService _revisions = null!;
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
        _transfers = new PostgreSqlSharedPackageTransferStore(_dataSource);
        _worldStore = new PostgreSqlSharedWorldStore(_dataSource);
        _worlds = new SharedWorldMetadataService(_worldStore, () => Now);
        _revisions = new SharedRevisionMetadataService(_worldStore, _worldStore);

        await ResetAsync();
        _manager = Steam("76561198000000001");
        var created = await _worlds.CreateSharedWorldAsync(
            _manager,
            new CreateSharedWorldCommand(
                WorldId.New(),
                "factorio",
                "Factory World",
                RevisionId.New(),
                RevisionId.New()));
        _world = Assert.IsType<SharedWorldMetadata>(created.World);
    }

    public async Task DisposeAsync()
    {
        await _dataSource.DisposeAsync();
    }

    [Fact]
    public async Task ActiveTransferRoundTripsAllResumeCriticalMetadata()
    {
        var transfer = NewEnvironmentTransfer();

        Assert.True(await _transfers.TryCreateAsync(transfer));
        var loaded = Assert.IsType<SharedPackageTransferRecord>(
            await _transfers.LoadAsync(transfer.Id));

        Assert.Equal(transfer, loaded);
        Assert.False(await _transfers.TryCreateAsync(transfer));
    }

    [Fact]
    public async Task StateTransferCanReferenceOnlyPersistedEnvironmentMetadata()
    {
        var environment = RevisionId.New();
        var transfer = NewStateTransfer(environment);

        await Assert.ThrowsAsync<PostgresException>(() => _transfers.TryCreateAsync(transfer));

        Assert.Equal(
            RecordRevisionMetadataStatus.Recorded,
            await _revisions.RecordVerifiedEnvironmentRevisionAsync(
                new SharedEnvironmentRevisionMetadata(
                    _world.WorldId,
                    environment,
                    _world.AdapterId,
                    "steam-manifest:factorio:stable",
                    null,
                    null,
                    _manager.Subject,
                    Now)));

        Assert.True(await _transfers.TryCreateAsync(transfer));
        Assert.Equal(
            environment,
            (await _transfers.LoadAsync(transfer.Id))?.RequiredEnvironmentRevisionId);
    }

    [Fact]
    public async Task StateTransitionIsOwnerAndExpectedStateCompareAndSet()
    {
        var transfer = NewEnvironmentTransfer();
        await _transfers.TryCreateAsync(transfer);
        var wrongOwner = new ExternalIdentityRef("steam", "76561198000000099");

        Assert.False(await _transfers.TrySetStateAsync(
            transfer.Id,
            wrongOwner,
            SharedPackageTransferState.Active,
            SharedPackageTransferState.Finalized,
            Now.AddMinutes(1)));
        Assert.False(await _transfers.TrySetStateAsync(
            transfer.Id,
            transfer.Owner,
            SharedPackageTransferState.IntegrityFailed,
            SharedPackageTransferState.Finalized,
            Now.AddMinutes(1)));
        Assert.True(await _transfers.TrySetStateAsync(
            transfer.Id,
            transfer.Owner,
            SharedPackageTransferState.Active,
            SharedPackageTransferState.Finalized,
            Now.AddMinutes(1)));

        var finalized = Assert.IsType<SharedPackageTransferRecord>(
            await _transfers.LoadAsync(transfer.Id));
        Assert.Equal(SharedPackageTransferState.Finalized, finalized.State);
        Assert.Equal(Now.AddMinutes(1), finalized.FinalizedAt);

        Assert.False(await _transfers.TrySetStateAsync(
            transfer.Id,
            transfer.Owner,
            SharedPackageTransferState.Active,
            SharedPackageTransferState.IntegrityFailed,
            Now.AddMinutes(2)));
    }

    [Fact]
    public async Task FailureStateDoesNotPretendTransferWasFinalized()
    {
        var transfer = NewEnvironmentTransfer();
        await _transfers.TryCreateAsync(transfer);

        Assert.True(await _transfers.TrySetStateAsync(
            transfer.Id,
            transfer.Owner,
            SharedPackageTransferState.Active,
            SharedPackageTransferState.IntegrityFailed,
            Now.AddMinutes(1)));

        var failed = Assert.IsType<SharedPackageTransferRecord>(
            await _transfers.LoadAsync(transfer.Id));
        Assert.Equal(SharedPackageTransferState.IntegrityFailed, failed.State);
        Assert.Null(failed.FinalizedAt);
    }

    private SharedPackageTransferRecord NewEnvironmentTransfer()
        => new(
            SharedPackageTransferId.New(),
            _world.WorldId,
            RevisionId.New(),
            SharedPackageKind.Environment,
            _world.AdapterId,
            _manager.Subject,
            $"packages/{_world.WorldId}/environment/test.package",
            $"provider-{Guid.NewGuid():N}",
            1024,
            HashA,
            null,
            SharedPackageTransferOptions.FirstReleasePartSizeBytes,
            1,
            Now,
            Now.AddHours(24),
            SharedPackageTransferState.Active);

    private SharedPackageTransferRecord NewStateTransfer(RevisionId environment)
        => new(
            SharedPackageTransferId.New(),
            _world.WorldId,
            RevisionId.New(),
            SharedPackageKind.State,
            _world.AdapterId,
            _manager.Subject,
            $"packages/{_world.WorldId}/state/test.package",
            $"provider-{Guid.NewGuid():N}",
            1024,
            HashA,
            environment,
            SharedPackageTransferOptions.FirstReleasePartSizeBytes,
            1,
            Now,
            Now.AddHours(24),
            SharedPackageTransferState.Active);

    private async Task ResetAsync()
    {
        await using var command = _dataSource.CreateCommand(
            "TRUNCATE TABLE steward_package_transfers, steward_world_invitations, " +
            "steward_state_revisions, steward_environment_revisions, steward_world_members, " +
            "steward_shared_worlds CASCADE;");
        await command.ExecuteNonQueryAsync();
    }

    private static VerifiedExternalIdentity Steam(string id)
        => new(new ExternalIdentityRef("steam", id));
}
