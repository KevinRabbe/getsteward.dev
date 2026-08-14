using SharedWorlds.Core.Abstractions;
using SharedWorlds.Core.Domain;
using SharedWorlds.Core.Environment;
using SharedWorlds.Core.Storage;
using SharedWorlds.Core.Worlds;

namespace SharedWorlds.Core.Tests;

public sealed class PreparedWorldFinalizationCoordinatorTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "safeworld-finalization-tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task ManagedDiscardProvesExactWorkspaceBeforeAdapterThenCoreDeletesRoot()
    {
        var managed = new ManagedWorkspaceStorage(Path.Combine(_root, "managed"));
        var record = CreateRecord(PreparedWorldRecoveryLocation.Managed());
        var workingDirectory = managed.Create(record.Id, record.AdapterId);
        await File.WriteAllTextAsync(Path.Combine(workingDirectory, "runtime.txt"), "runtime");
        var adapter = new FinalizationAdapter(record.AdapterId)
        {
            OnFinalize = world => Assert.True(Directory.Exists(world.WorkingDirectory))
        };
        var prepared = CreatePrepared(
            adapter,
            workingDirectory,
            PreparedWorldRecoveryLocation.Managed());
        var coordinator = new PreparedWorldFinalizationCoordinator(managed);

        await coordinator.FinalizeAsync(
            record,
            adapter,
            prepared,
            PreparedWorldDisposition.Discard);

        Assert.True(adapter.FinalizeCalled);
        Assert.False(Directory.Exists(workingDirectory));
    }

    [Fact]
    public async Task ManagedPreserveRunsAdapterButKeepsCoreOwnedWorkspace()
    {
        var managed = new ManagedWorkspaceStorage(Path.Combine(_root, "managed"));
        var record = CreateRecord(PreparedWorldRecoveryLocation.Managed());
        var workingDirectory = managed.Create(record.Id, record.AdapterId);
        var adapter = new FinalizationAdapter(record.AdapterId);
        var prepared = CreatePrepared(
            adapter,
            workingDirectory,
            PreparedWorldRecoveryLocation.Managed());
        var coordinator = new PreparedWorldFinalizationCoordinator(managed);

        await coordinator.FinalizeAsync(
            record,
            adapter,
            prepared,
            PreparedWorldDisposition.PreserveForRecovery);

        Assert.True(adapter.FinalizeCalled);
        Assert.True(Directory.Exists(workingDirectory));
    }

    [Fact]
    public async Task WrongManagedPathIsRejectedBeforeAdapterCanTouchRuntime()
    {
        var managed = new ManagedWorkspaceStorage(Path.Combine(_root, "managed"));
        var record = CreateRecord(PreparedWorldRecoveryLocation.Managed());
        _ = managed.Create(record.Id, record.AdapterId);
        var wrongPath = Path.Combine(_root, "wrong", WorkspaceId.New().ToString());
        Directory.CreateDirectory(wrongPath);
        var adapter = new FinalizationAdapter(record.AdapterId);
        var prepared = CreatePrepared(
            adapter,
            wrongPath,
            PreparedWorldRecoveryLocation.Managed());
        var coordinator = new PreparedWorldFinalizationCoordinator(managed);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            coordinator.FinalizeAsync(
                record,
                adapter,
                prepared,
                PreparedWorldDisposition.Discard));

        Assert.False(adapter.FinalizeCalled);
        Assert.True(Directory.Exists(wrongPath));
    }

    [Fact]
    public async Task NativeDiscardNeverUsesGenericManagedDeletion()
    {
        var managed = new ManagedWorkspaceStorage(Path.Combine(_root, "managed"));
        var location = PreparedWorldRecoveryLocation.Native(
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["worldId"] = "native-world"
            });
        var record = CreateRecord(location);
        var nativePath = Path.Combine(_root, "native-world");
        Directory.CreateDirectory(nativePath);
        var adapter = new FinalizationAdapter(record.AdapterId);
        var prepared = CreatePrepared(adapter, nativePath, location);
        var coordinator = new PreparedWorldFinalizationCoordinator(managed);

        await coordinator.FinalizeAsync(
            record,
            adapter,
            prepared,
            PreparedWorldDisposition.Discard);

        Assert.True(adapter.FinalizeCalled);
        Assert.True(Directory.Exists(nativePath));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private static WorkspaceRecoveryRecord CreateRecord(PreparedWorldRecoveryLocation location)
    {
        var now = DateTimeOffset.UtcNow;
        return new WorkspaceRecoveryRecord(
            WorkspaceId.New(),
            WorldId.New(),
            RevisionId.New(),
            "finalization-test",
            string.Empty,
            new UserIdentity("test", "user", "Test User"),
            now,
            now,
            WorkspaceRecoveryStatus.CleanupPending,
            EnvironmentRevisionId: RevisionId.New(),
            RecoveryLocation: location);
    }

    private static PreparedWorld CreatePrepared(
        FinalizationAdapter adapter,
        string workingDirectory,
        PreparedWorldRecoveryLocation location)
        => new(
            adapter.Installation,
            workingDirectory,
            new EnvironmentManifest(
                1,
                adapter.Id,
                "1.0",
                [],
                new Dictionary<string, string>()),
            RecoveryLocation: location);

    private sealed class FinalizationAdapter : IGameAdapter
    {
        public FinalizationAdapter(string id)
        {
            Id = id;
            Installation = new GameInstallation("test", Path.GetTempPath(), "test");
        }

        public string Id { get; }
        public string DisplayName => "Finalization Test";
        public GameAdapterCapabilities Capabilities => GameAdapterCapabilities.None;
        public GameInstallation Installation { get; }
        public bool FinalizeCalled { get; private set; }
        public Action<PreparedWorld>? OnFinalize { get; init; }

        public Task<IReadOnlyList<GameInstallation>> DiscoverInstallationsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<GameInstallation>>([Installation]);

        public Task<IReadOnlyList<DetectedWorld>> DiscoverWorldsAsync(GameInstallation installation, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<EnvironmentManifest> InspectEnvironmentAsync(GameInstallation installation, DetectedWorld world, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<CapturedState> CaptureDetectedWorldAsync(GameInstallation installation, DetectedWorld world, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<PreparedWorld> PrepareEnvironmentAsync(GameInstallation installation, EnvironmentManifest requiredEnvironment, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<CapturedState> CaptureStateAsync(PreparedWorld world, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task RestoreStateAsync(PreparedWorld world, StatePackage state, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task FinalizePreparedWorldAsync(PreparedWorld world, PreparedWorldDisposition disposition, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            OnFinalize?.Invoke(world);
            FinalizeCalled = true;
            return Task.CompletedTask;
        }
    }
}
