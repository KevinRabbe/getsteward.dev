using SharedWorlds.Core.Abstractions;
using SharedWorlds.Core.Domain;
using SharedWorlds.Core.Environment;
using SharedWorlds.Core.Errors;
using SharedWorlds.Core.Worlds;

namespace SharedWorlds.Core.Tests;

public sealed class WorldJoinServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"sharedworlds-join-tests-{Guid.NewGuid():N}");

    [Fact]
    public async Task Join_UsesEnvironmentAndHostConnection_WithoutMaterializingCanonicalState()
    {
        var adapter = new FakeJoinAdapter(_root);
        var storage = SeedWorld(adapter, currentStateRevisionId: null);
        var world = storage.World!;
        var service = new WorldJoinService(storage);
        var host = new HostConnection("203.0.113.20", 34197, "join-token");

        await service.JoinAsync(
            world.Id,
            adapter,
            adapter.Installation,
            host);

        Assert.Equal(1, storage.WorldLoadCount);
        Assert.Equal(1, storage.EnvironmentLoadCount);
        Assert.Equal(0, storage.StateRevisionLoadCount);
        Assert.Equal(0, storage.StatePayloadOpenCount);
        Assert.Equal(1, adapter.VerifyCount);
        Assert.Equal(1, adapter.PrepareCount);
        Assert.Equal(1, adapter.LaunchClientCount);
        Assert.Equal(1, adapter.WaitCount);
        Assert.Same(host, adapter.LastHostConnection);
        Assert.Equal(PreparedWorldDisposition.Discard, adapter.LastFinalizationDisposition);
        Assert.NotNull(adapter.LastPreparedWorkspacePath);
        Assert.False(Directory.Exists(adapter.LastPreparedWorkspacePath));
    }

    [Fact]
    public async Task Join_RejectsAdapterWithoutAutomaticJoin_BeforeStorageOrPreparation()
    {
        var adapter = new FakeJoinAdapter(_root)
        {
            AdvertiseAutomaticJoin = false
        };
        var storage = SeedWorld(adapter, currentStateRevisionId: RevisionId.New());
        var service = new WorldJoinService(storage);

        await Assert.ThrowsAsync<NotSupportedException>(() => service.JoinAsync(
            storage.World!.Id,
            adapter,
            adapter.Installation,
            new HostConnection("203.0.113.20", 34197)));

        Assert.Equal(0, storage.WorldLoadCount);
        Assert.Equal(0, adapter.VerifyCount);
        Assert.Equal(0, adapter.PrepareCount);
        Assert.Equal(0, adapter.LaunchClientCount);
    }

    [Fact]
    public async Task Join_FailsBeforePreparation_WhenExactEnvironmentIsNotReady()
    {
        var adapter = new FakeJoinAdapter(_root)
        {
            Verification = EnvironmentVerificationReport.Unsupported(
                "Exact environment is unavailable on this device.")
        };
        var storage = SeedWorld(adapter, currentStateRevisionId: RevisionId.New());
        var service = new WorldJoinService(storage);

        await Assert.ThrowsAsync<EnvironmentReproductionException>(() => service.JoinAsync(
            storage.World!.Id,
            adapter,
            adapter.Installation,
            new HostConnection("203.0.113.20", 34197)));

        Assert.Equal(1, adapter.VerifyCount);
        Assert.Equal(0, adapter.PrepareCount);
        Assert.Equal(0, adapter.LaunchClientCount);
        Assert.Null(adapter.LastFinalizationDisposition);
    }

    [Fact]
    public async Task Join_DiscardsPreparedWorkspace_WhenAdapterBlocksSpecificHost()
    {
        var adapter = new FakeJoinAdapter(_root)
        {
            JoinCapability = JoinCapabilityResult.BlockedByIdentityLimitation(
                "This host requires a game identity path that is not supported yet.")
        };
        var storage = SeedWorld(adapter, currentStateRevisionId: RevisionId.New());
        var service = new WorldJoinService(storage);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => service.JoinAsync(
            storage.World!.Id,
            adapter,
            adapter.Installation,
            new HostConnection("203.0.113.20", 34197)));

        Assert.Contains("identity", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, adapter.PrepareCount);
        Assert.Equal(0, adapter.LaunchClientCount);
        Assert.Equal(PreparedWorldDisposition.Discard, adapter.LastFinalizationDisposition);
        Assert.NotNull(adapter.LastPreparedWorkspacePath);
        Assert.False(Directory.Exists(adapter.LastPreparedWorkspacePath));
    }

    [Fact]
    public async Task Join_DiscardsReadOnlyWorkspace_WhenClientLaunchFails()
    {
        var adapter = new FakeJoinAdapter(_root)
        {
            FailLaunch = true
        };
        var storage = SeedWorld(adapter, currentStateRevisionId: RevisionId.New());
        var service = new WorldJoinService(storage);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => service.JoinAsync(
            storage.World!.Id,
            adapter,
            adapter.Installation,
            new HostConnection("203.0.113.20", 34197)));

        Assert.Equal("Injected client launch failure.", exception.Message);
        Assert.Equal(1, adapter.LaunchClientCount);
        Assert.Equal(0, adapter.WaitCount);
        Assert.Equal(PreparedWorldDisposition.Discard, adapter.LastFinalizationDisposition);
        Assert.NotNull(adapter.LastPreparedWorkspacePath);
        Assert.False(Directory.Exists(adapter.LastPreparedWorkspacePath));
    }

    [Fact]
    public async Task Join_RejectsWorldOwnedByDifferentAdapter_BeforeEnvironmentWork()
    {
        var adapter = new FakeJoinAdapter(_root);
        var storage = SeedWorld(
            adapter,
            currentStateRevisionId: RevisionId.New(),
            worldAdapterId: "different-adapter");
        var service = new WorldJoinService(storage);

        await Assert.ThrowsAsync<AdapterMismatchException>(() => service.JoinAsync(
            storage.World!.Id,
            adapter,
            adapter.Installation,
            new HostConnection("203.0.113.20", 34197)));

        Assert.Equal(0, storage.EnvironmentLoadCount);
        Assert.Equal(0, adapter.VerifyCount);
        Assert.Equal(0, adapter.PrepareCount);
    }

    private static RecordingStorage SeedWorld(
        FakeJoinAdapter adapter,
        RevisionId? currentStateRevisionId,
        string? worldAdapterId = null)
    {
        var user = new UserIdentity("local", "tester", "Tester");
        var worldId = WorldId.New();
        var environmentId = RevisionId.New();
        var world = new World(
            worldId,
            "Join World",
            worldAdapterId ?? adapter.Id,
            [user],
            environmentId,
            currentStateRevisionId)
        {
            SharingMode = WorldSharingMode.Shared
        };
        var environment = new EnvironmentRevision(
            environmentId,
            worldId,
            ParentRevisionId: null,
            DateTimeOffset.UtcNow,
            user,
            adapter.Manifest);

        return new RecordingStorage
        {
            World = world,
            EnvironmentRevision = environment
        };
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

    private sealed class FakeJoinAdapter : IGameAdapter
    {
        private readonly string _root;

        public FakeJoinAdapter(string root)
        {
            _root = root;
            Directory.CreateDirectory(root);
            Installation = new GameInstallation("fake-installation", root, "test");
            Manifest = new EnvironmentManifest(
                1,
                Id,
                "1.0.0",
                [],
                new Dictionary<string, string>());
        }

        public string Id => "fake";
        public string DisplayName => "Fake Game";
        public GameAdapterCapabilities Capabilities => AdvertiseAutomaticJoin
            ? GameAdapterCapabilities.AutomaticClientJoin | GameAdapterCapabilities.ExactGameVersion
            : GameAdapterCapabilities.ExactGameVersion;
        public bool AdvertiseAutomaticJoin { get; init; } = true;
        public bool FailLaunch { get; init; }
        public EnvironmentVerificationReport Verification { get; init; } = EnvironmentVerificationReport.Ready();
        public JoinCapabilityResult JoinCapability { get; init; } = JoinCapabilityResult.SupportedAutomatic();
        public GameInstallation Installation { get; }
        public EnvironmentManifest Manifest { get; }
        public int VerifyCount { get; private set; }
        public int PrepareCount { get; private set; }
        public int LaunchClientCount { get; private set; }
        public int WaitCount { get; private set; }
        public HostConnection? LastHostConnection { get; private set; }
        public string? LastPreparedWorkspacePath { get; private set; }
        public PreparedWorldDisposition? LastFinalizationDisposition { get; private set; }

        public Task<IReadOnlyList<GameInstallation>> DiscoverInstallationsAsync(
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<DetectedWorld>> DiscoverWorldsAsync(
            GameInstallation installation,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

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

        public Task<EnvironmentVerificationReport> VerifyEnvironmentAsync(
            GameInstallation installation,
            EnvironmentManifest requiredEnvironment,
            CancellationToken cancellationToken = default)
        {
            VerifyCount++;
            return Task.FromResult(Verification);
        }

        public Task<PreparedWorld> PrepareEnvironmentAsync(
            GameInstallation installation,
            EnvironmentManifest requiredEnvironment,
            CancellationToken cancellationToken = default)
        {
            PrepareCount++;
            var workspace = Path.Combine(_root, $"join-{Guid.NewGuid():N}");
            Directory.CreateDirectory(workspace);
            LastPreparedWorkspacePath = workspace;
            return Task.FromResult(new PreparedWorld(
                installation,
                workspace,
                requiredEnvironment));
        }

        public Task<CapturedState> CaptureStateAsync(
            PreparedWorld world,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task RestoreStateAsync(
            PreparedWorld world,
            StatePackage state,
            CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("Join must never restore canonical state.");

        public Task<GameSessionHandle> LaunchLocalAsync(
            PreparedWorld world,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<GameSessionHandle> LaunchHostAsync(
            PreparedWorld world,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<JoinCapabilityResult> GetJoinCapabilityAsync(
            PreparedWorld world,
            HostConnection host,
            CancellationToken cancellationToken = default)
            => Task.FromResult(JoinCapability);

        public Task<GameSessionHandle> LaunchClientAsync(
            PreparedWorld world,
            HostConnection host,
            CancellationToken cancellationToken = default)
        {
            LaunchClientCount++;
            LastHostConnection = host;
            if (FailLaunch)
            {
                throw new InvalidOperationException("Injected client launch failure.");
            }

            return Task.FromResult(new GameSessionHandle(12345, DateTimeOffset.UtcNow));
        }

        public Task WaitForSessionEndAsync(
            GameSessionHandle session,
            CancellationToken cancellationToken = default)
        {
            WaitCount++;
            return Task.CompletedTask;
        }

        public Task FinalizePreparedWorldAsync(
            PreparedWorld world,
            PreparedWorldDisposition disposition,
            CancellationToken cancellationToken = default)
        {
            LastFinalizationDisposition = disposition;
            if (disposition == PreparedWorldDisposition.Discard &&
                Directory.Exists(world.WorkingDirectory))
            {
                Directory.Delete(world.WorkingDirectory, recursive: true);
            }

            return Task.CompletedTask;
        }
    }

    private sealed class RecordingStorage : IWorldStorage
    {
        public World? World { get; init; }
        public EnvironmentRevision? EnvironmentRevision { get; init; }
        public int WorldLoadCount { get; private set; }
        public int EnvironmentLoadCount { get; private set; }
        public int StateRevisionLoadCount { get; private set; }
        public int StatePayloadOpenCount { get; private set; }

        public Task SaveWorldAsync(
            World world,
            CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("Join must never save canonical World metadata.");

        public Task<World?> LoadWorldAsync(
            WorldId worldId,
            CancellationToken cancellationToken = default)
        {
            WorldLoadCount++;
            return Task.FromResult(World?.Id == worldId ? World : null);
        }

        public Task StoreEnvironmentRevisionAsync(
            EnvironmentRevision revision,
            CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("Join must never write environment revisions.");

        public Task<EnvironmentRevision?> LoadEnvironmentRevisionAsync(
            WorldId worldId,
            RevisionId revisionId,
            CancellationToken cancellationToken = default)
        {
            EnvironmentLoadCount++;
            var matches = EnvironmentRevision is not null &&
                          EnvironmentRevision.WorldId == worldId &&
                          EnvironmentRevision.Id == revisionId;
            return Task.FromResult(matches ? EnvironmentRevision : null);
        }

        public Task StoreRevisionAsync(
            StateRevision revision,
            Stream package,
            CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("Join must never store canonical state.");

        public Task<StateRevision?> LoadStateRevisionAsync(
            WorldId worldId,
            RevisionId revisionId,
            CancellationToken cancellationToken = default)
        {
            StateRevisionLoadCount++;
            throw new InvalidOperationException("Join must never load canonical state metadata.");
        }

        public Task<Stream> OpenRevisionAsync(
            WorldId worldId,
            RevisionId revisionId,
            CancellationToken cancellationToken = default)
        {
            StatePayloadOpenCount++;
            throw new InvalidOperationException("Join must never materialize canonical state payloads.");
        }
    }
}
