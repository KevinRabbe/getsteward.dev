using SharedWorlds.Core.Abstractions;
using SharedWorlds.Core.Domain;
using SharedWorlds.Core.Environment;
using SharedWorlds.Core.Storage;
using SharedWorlds.Core.Worlds;

namespace SharedWorlds.Core.Tests;

public sealed class PreparedWorldRecoveryResolverTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"safeworld-recovery-resolver-tests-{Guid.NewGuid():N}");

    [Fact]
    public void ManagedRecoveryIgnoresPersistedLegacyPathAndResolvesFromWorkspaceId()
    {
        var storage = new ManagedWorkspaceStorage(_root);
        var resolver = new PreparedWorldRecoveryResolver(storage);
        var adapter = new TestAdapter();
        var record = CreateRecord(adapter.Id) with
        {
            WorkingDirectory = "stale-machine-specific-path",
            RecoveryLocation = PreparedWorldRecoveryLocation.Managed()
        };

        var prepared = resolver.Resolve(
            record,
            adapter,
            Installation,
            EnvironmentFor(adapter.Id),
            "World");

        Assert.Equal(
            storage.GetWorkspaceDirectory(record.Id, adapter.Id),
            prepared.WorkingDirectory);
        Assert.Equal(record.RecoveryLocation, prepared.RecoveryLocation);
    }

    [Fact]
    public void LegacyRecoveryStillUsesAbsolutePathDuringMigrationWindow()
    {
        var resolver = new PreparedWorldRecoveryResolver(
            new ManagedWorkspaceStorage(_root));
        var adapter = new TestAdapter();
        var record = CreateRecord(adapter.Id) with
        {
            WorkingDirectory = Path.GetFullPath(Path.Combine(_root, "legacy")),
            RecoveryLocation = null
        };

        var prepared = resolver.Resolve(
            record,
            adapter,
            Installation,
            EnvironmentFor(adapter.Id));

        Assert.Equal(record.WorkingDirectory, prepared.WorkingDirectory);
        Assert.Null(prepared.RecoveryLocation);
    }

    [Fact]
    public void NativeRecoveryDelegatesStableIdentityToExplicitAdapterCapability()
    {
        var resolver = new PreparedWorldRecoveryResolver(
            new ManagedWorkspaceStorage(_root));
        var adapter = new NativeTestAdapter();
        var location = PreparedWorldRecoveryLocation.Native(
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["worldId"] = "native-42"
            });
        var record = CreateRecord(adapter.Id) with
        {
            WorkingDirectory = "stale-native-path",
            RecoveryLocation = location
        };

        var prepared = resolver.Resolve(
            record,
            adapter,
            Installation,
            EnvironmentFor(adapter.Id));

        Assert.Equal(
            Path.GetFullPath(Path.Combine(Installation.RootPath, "native-42")),
            prepared.WorkingDirectory);
        Assert.Equal(location, prepared.RecoveryLocation);
    }

    [Fact]
    public void NativeRecoveryFailsClosedWhenAdapterCannotReconstructIt()
    {
        var resolver = new PreparedWorldRecoveryResolver(
            new ManagedWorkspaceStorage(_root));
        var adapter = new TestAdapter();
        var record = CreateRecord(adapter.Id) with
        {
            RecoveryLocation = PreparedWorldRecoveryLocation.Native(
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["worldId"] = "native-42"
                })
        };

        Assert.Throws<InvalidOperationException>(() => resolver.Resolve(
            record,
            adapter,
            Installation,
            EnvironmentFor(adapter.Id)));
    }

    private static readonly GameInstallation Installation = new(
        "install",
        Path.GetFullPath("game-root"),
        "test");

    private static EnvironmentManifest EnvironmentFor(string adapterId)
        => new(
            SchemaVersion: 1,
            AdapterId: adapterId,
            GameVersion: "1",
            Components: [],
            Configuration: new Dictionary<string, string>(StringComparer.Ordinal));

    private WorkspaceRecoveryRecord CreateRecord(string adapterId)
    {
        var now = DateTimeOffset.UtcNow;
        return new WorkspaceRecoveryRecord(
            WorkspaceId.New(),
            WorldId.New(),
            RevisionId.New(),
            adapterId,
            Path.GetFullPath(Path.Combine(_root, "legacy-placeholder")),
            new UserIdentity("test", "user"),
            now,
            now,
            WorkspaceRecoveryStatus.RecoveryPending,
            EnvironmentRevisionId: RevisionId.New());
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private class TestAdapter : IGameAdapter
    {
        public virtual string Id => "test";
        public string DisplayName => "Test";
        public GameAdapterCapabilities Capabilities => GameAdapterCapabilities.None;

        public Task<IReadOnlyList<GameInstallation>> DiscoverInstallationsAsync(
            CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<GameInstallation>>([]);

        public Task<IReadOnlyList<DetectedWorld>> DiscoverWorldsAsync(
            GameInstallation installation,
            CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<DetectedWorld>>([]);

        public Task<EnvironmentManifest> InspectEnvironmentAsync(
            GameInstallation installation,
            DetectedWorld world,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<CapturedState> CaptureDetectedWorldAsync(
            GameInstallation installation,
            DetectedWorld world,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<PreparedWorld> PrepareEnvironmentAsync(
            GameInstallation installation,
            EnvironmentManifest requiredEnvironment,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<CapturedState> CaptureStateAsync(
            PreparedWorld world,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task RestoreStateAsync(
            PreparedWorld world,
            StatePackage state,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task FinalizePreparedWorldAsync(
            PreparedWorld world,
            PreparedWorldDisposition disposition,
            CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }

    private sealed class NativeTestAdapter : TestAdapter, INativePreparedWorldRecoveryAdapter
    {
        public override string Id => "native-test";

        public PreparedWorld ResolveNativePreparedWorld(
            GameInstallation installation,
            EnvironmentManifest environment,
            PreparedWorldRecoveryLocation recoveryLocation,
            string? displayName = null)
        {
            recoveryLocation.Validate();
            var worldId = recoveryLocation.NativeIdentity!["worldId"];
            return new PreparedWorld(
                installation,
                Path.GetFullPath(Path.Combine(installation.RootPath, worldId)),
                environment,
                displayName);
        }
    }
}
