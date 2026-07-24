using SharedWorlds.Core.Abstractions;
using SharedWorlds.Core.Domain;
using SharedWorlds.Core.Environment;
using SharedWorlds.Core.Worlds;
using SharedWorlds.Infrastructure.Recovery;
using SharedWorlds.Infrastructure.Sessions;

namespace SharedWorlds.Infrastructure.Tests.Recovery;

public sealed class LocalPendingWorkspaceRecoveryServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "steward-local-pending-recovery-tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task RetryCapturesCandidateCommitsAndCleansWorkspace()
    {
        var fixture = CreateFixture();
        var service = CreateService(fixture);

        var updated = await service.RetryAsync(
            fixture.World.Id,
            fixture.Adapter,
            fixture.Adapter.Installation,
            fixture.User);

        Assert.Equal(fixture.Record.CandidateStateRevisionId, updated.CurrentStateRevisionId);
        Assert.Equal(updated, fixture.Storage.World);
        Assert.Equal(1, fixture.Adapter.CaptureCount);
        Assert.Equal(1, fixture.Adapter.FinalizeCount);
        Assert.Empty(fixture.Recovery.Records);
        Assert.False(Directory.Exists(fixture.Record.WorkingDirectory));

        var candidate = Assert.Single(fixture.Storage.Revisions.Values);
        Assert.Equal(fixture.Record.BaseStateRevisionId, candidate.ParentRevisionId);
        Assert.Equal(fixture.Record.CandidateStateRevisionId, candidate.Id);
    }

    [Fact]
    public async Task LostLocalCommitResponseIsRecoveredWithoutRecapturing()
    {
        var fixture = CreateFixture();
        fixture.Storage.ThrowAfterNextWorldSave = true;
        var service = CreateService(fixture);

        await Assert.ThrowsAsync<IOException>(() => service.RetryAsync(
            fixture.World.Id,
            fixture.Adapter,
            fixture.Adapter.Installation,
            fixture.User));

        Assert.Equal(fixture.Record.CandidateStateRevisionId, fixture.Storage.World.CurrentStateRevisionId);
        Assert.Equal(1, fixture.Adapter.CaptureCount);
        Assert.Single(fixture.Recovery.Records);
        Assert.True(Directory.Exists(fixture.Record.WorkingDirectory));

        var recovered = await service.RetryAsync(
            fixture.World.Id,
            fixture.Adapter,
            fixture.Adapter.Installation,
            fixture.User);

        Assert.Equal(fixture.Record.CandidateStateRevisionId, recovered.CurrentStateRevisionId);
        Assert.Equal(1, fixture.Adapter.CaptureCount);
        Assert.Equal(1, fixture.Adapter.FinalizeCount);
        Assert.Empty(fixture.Recovery.Records);
    }

    [Fact]
    public async Task ExistingCandidateMetadataAndPackageAreReusedWithoutRecapture()
    {
        var fixture = CreateFixture();
        var candidateId = fixture.Record.CandidateStateRevisionId!.Value;
        fixture.Storage.Revisions[candidateId] = new StateRevision(
            candidateId,
            fixture.World.Id,
            fixture.Record.BaseStateRevisionId,
            DateTimeOffset.UtcNow,
            fixture.User,
            fixture.Adapter.Id,
            "existing-package");
        fixture.Storage.Payloads[candidateId] = [1, 2, 3, 4];
        var service = CreateService(fixture);

        var recovered = await service.RetryAsync(
            fixture.World.Id,
            fixture.Adapter,
            fixture.Adapter.Installation,
            fixture.User);

        Assert.Equal(candidateId, recovered.CurrentStateRevisionId);
        Assert.Equal(0, fixture.Adapter.CaptureCount);
        Assert.Empty(fixture.Recovery.Records);
    }

    [Fact]
    public async Task ExistingCandidateWithoutPackageIsNeverPromoted()
    {
        var fixture = CreateFixture();
        var candidateId = fixture.Record.CandidateStateRevisionId!.Value;
        fixture.Storage.Revisions[candidateId] = new StateRevision(
            candidateId,
            fixture.World.Id,
            fixture.Record.BaseStateRevisionId,
            DateTimeOffset.UtcNow,
            fixture.User,
            fixture.Adapter.Id,
            "missing-package");
        var originalHead = fixture.Storage.World.CurrentStateRevisionId;
        var service = CreateService(fixture);

        var exception = await Assert.ThrowsAsync<LocalPendingWorkspaceRecoveryException>(() =>
            service.RetryAsync(
                fixture.World.Id,
                fixture.Adapter,
                fixture.Adapter.Installation,
                fixture.User));

        Assert.Equal("CandidatePackageInvalid", exception.Code);
        Assert.Equal(originalHead, fixture.Storage.World.CurrentStateRevisionId);
        Assert.Equal(0, fixture.Adapter.CaptureCount);
        Assert.Equal(0, fixture.Adapter.FinalizeCount);
        Assert.Single(fixture.Recovery.Records);
        Assert.True(Directory.Exists(fixture.Record.WorkingDirectory));
    }

    [Fact]
    public async Task CanonicalCandidateWithoutPackageKeepsRecoveryEvidence()
    {
        var fixture = CreateFixture();
        var candidateId = fixture.Record.CandidateStateRevisionId!.Value;
        fixture.Storage.World = fixture.Storage.World with
        {
            CurrentStateRevisionId = candidateId
        };
        fixture.Storage.Revisions[candidateId] = new StateRevision(
            candidateId,
            fixture.World.Id,
            fixture.Record.BaseStateRevisionId,
            DateTimeOffset.UtcNow,
            fixture.User,
            fixture.Adapter.Id,
            "missing-canonical-package");
        var service = CreateService(fixture);

        var exception = await Assert.ThrowsAsync<LocalPendingWorkspaceRecoveryException>(() =>
            service.RetryAsync(
                fixture.World.Id,
                fixture.Adapter,
                fixture.Adapter.Installation,
                fixture.User));

        Assert.Equal("CandidatePackageInvalid", exception.Code);
        Assert.Equal(candidateId, fixture.Storage.World.CurrentStateRevisionId);
        Assert.Equal(0, fixture.Adapter.FinalizeCount);
        Assert.Single(fixture.Recovery.Records);
        Assert.True(Directory.Exists(fixture.Record.WorkingDirectory));
    }

    [Fact]
    public async Task DivergedCanonicalHeadIsNeverOverwritten()
    {
        var fixture = CreateFixture();
        fixture.Storage.World = fixture.Storage.World with
        {
            CurrentStateRevisionId = RevisionId.New()
        };
        var service = CreateService(fixture);

        var exception = await Assert.ThrowsAsync<LocalPendingWorkspaceRecoveryException>(() =>
            service.RetryAsync(
                fixture.World.Id,
                fixture.Adapter,
                fixture.Adapter.Installation,
                fixture.User));

        Assert.Equal("CanonicalHeadDiverged", exception.Code);
        Assert.Equal(0, fixture.Adapter.CaptureCount);
        Assert.Single(fixture.Recovery.Records);
        Assert.True(Directory.Exists(fixture.Record.WorkingDirectory));
    }

    [Fact]
    public async Task CleanupFailureBecomesCleanupPendingAfterCanonicalCommit()
    {
        var fixture = CreateFixture();
        fixture.Adapter.ThrowOnFinalize = true;
        var service = CreateService(fixture);

        var exception = await Assert.ThrowsAsync<LocalPendingWorkspaceRecoveryException>(() =>
            service.RetryAsync(
                fixture.World.Id,
                fixture.Adapter,
                fixture.Adapter.Installation,
                fixture.User));

        Assert.Equal("CleanupPending", exception.Code);
        Assert.Equal(fixture.Record.CandidateStateRevisionId, fixture.Storage.World.CurrentStateRevisionId);
        Assert.Equal(WorkspaceRecoveryStatus.CleanupPending, Assert.Single(fixture.Recovery.Records).Status);
        Assert.True(Directory.Exists(fixture.Record.WorkingDirectory));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private LocalPendingWorkspaceRecoveryService CreateService(Fixture fixture)
        => new(
            fixture.Storage,
            new LocalWorldSessionCoordinator(),
            fixture.Recovery,
            new ManagedWritableSessionGate());

    private Fixture CreateFixture()
    {
        Directory.CreateDirectory(_root);
        var workspace = Path.Combine(_root, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workspace);
        File.WriteAllText(Path.Combine(workspace, "world-state.txt"), "recovered-state");

        var worldId = WorldId.New();
        var baseStateId = RevisionId.New();
        var environmentId = RevisionId.New();
        var candidateId = RevisionId.New();
        var user = new UserIdentity("local", "user", "Local User");
        var manifest = new EnvironmentManifest(
            1,
            "test-adapter",
            "1.0.0",
            [],
            new Dictionary<string, string>());
        var environment = new EnvironmentRevision(
            environmentId,
            worldId,
            ParentRevisionId: null,
            DateTimeOffset.UtcNow,
            user,
            manifest);
        var world = new World(
            worldId,
            "Recovery World",
            "test-adapter",
            [user],
            environmentId,
            baseStateId);
        var record = new WorkspaceRecoveryRecord(
            WorkspaceId.New(),
            worldId,
            baseStateId,
            "test-adapter",
            workspace,
            user,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            WorkspaceRecoveryStatus.RecoveryPending,
            CandidateStateRevisionId: candidateId,
            EnvironmentRevisionId: environmentId);
        var storage = new RecoveryStorage(world, environment);
        var recovery = new RecoveryStore(record);
        var adapter = new RecoveryAdapter(_root);
        return new Fixture(world, record, user, storage, recovery, adapter);
    }

    private sealed record Fixture(
        World World,
        WorkspaceRecoveryRecord Record,
        UserIdentity User,
        RecoveryStorage Storage,
        RecoveryStore Recovery,
        RecoveryAdapter Adapter);

    private sealed class RecoveryStorage : IWorldStorage
    {
        private readonly EnvironmentRevision _environment;

        public RecoveryStorage(World world, EnvironmentRevision environment)
        {
            World = world;
            _environment = environment;
        }

        public World World { get; set; }
        public Dictionary<RevisionId, StateRevision> Revisions { get; } = [];
        public Dictionary<RevisionId, byte[]> Payloads { get; } = [];
        public bool ThrowAfterNextWorldSave { get; set; }

        public Task SaveWorldAsync(World world, CancellationToken cancellationToken = default)
        {
            World = world;
            if (ThrowAfterNextWorldSave)
            {
                ThrowAfterNextWorldSave = false;
                throw new IOException("Injected lost local commit response.");
            }

            return Task.CompletedTask;
        }

        public Task<World?> LoadWorldAsync(
            WorldId worldId,
            CancellationToken cancellationToken = default)
            => Task.FromResult<World?>(World.Id == worldId ? World : null);

        public Task StoreEnvironmentRevisionAsync(
            EnvironmentRevision revision,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<EnvironmentRevision?> LoadEnvironmentRevisionAsync(
            WorldId worldId,
            RevisionId revisionId,
            CancellationToken cancellationToken = default)
            => Task.FromResult<EnvironmentRevision?>(
                _environment.WorldId == worldId && _environment.Id == revisionId
                    ? _environment
                    : null);

        public async Task StoreRevisionAsync(
            StateRevision revision,
            Stream package,
            CancellationToken cancellationToken = default)
        {
            using var sink = new MemoryStream();
            await package.CopyToAsync(sink, cancellationToken);
            Revisions[revision.Id] = revision;
            Payloads[revision.Id] = sink.ToArray();
        }

        public Task<StateRevision?> LoadStateRevisionAsync(
            WorldId worldId,
            RevisionId revisionId,
            CancellationToken cancellationToken = default)
            => Task.FromResult<StateRevision?>(
                Revisions.TryGetValue(revisionId, out var revision) && revision.WorldId == worldId
                    ? revision
                    : null);

        public Task<Stream> OpenRevisionAsync(
            WorldId worldId,
            RevisionId revisionId,
            CancellationToken cancellationToken = default)
        {
            if (!Revisions.TryGetValue(revisionId, out var revision) || revision.WorldId != worldId)
            {
                throw new FileNotFoundException("Test state revision does not exist.");
            }

            if (!Payloads.TryGetValue(revisionId, out var payload))
            {
                throw new FileNotFoundException("Test state payload does not exist.");
            }

            Stream stream = new MemoryStream(payload, writable: false);
            return Task.FromResult(stream);
        }
    }

    private sealed class RecoveryStore : IWorkspaceRecoveryStore
    {
        public RecoveryStore(WorkspaceRecoveryRecord record)
        {
            Records.Add(record);
        }

        public List<WorkspaceRecoveryRecord> Records { get; } = [];

        public Task SaveAsync(
            WorkspaceRecoveryRecord record,
            CancellationToken cancellationToken = default)
        {
            Records.RemoveAll(existing => existing.Id == record.Id);
            Records.Add(record);
            return Task.CompletedTask;
        }

        public Task RemoveAsync(
            WorkspaceId workspaceId,
            CancellationToken cancellationToken = default)
        {
            Records.RemoveAll(record => record.Id == workspaceId);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<WorkspaceRecoveryRecord>> ListAsync(
            CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<WorkspaceRecoveryRecord>>(Records.ToArray());
    }

    private sealed class RecoveryAdapter : IGameAdapter
    {
        private readonly string _root;

        public RecoveryAdapter(string root)
        {
            _root = root;
            Installation = new GameInstallation("test-installation", root, "test");
        }

        public string Id => "test-adapter";
        public string DisplayName => "Recovery Test Adapter";
        public GameAdapterCapabilities Capabilities => GameAdapterCapabilities.None;
        public GameInstallation Installation { get; }
        public int CaptureCount { get; private set; }
        public int FinalizeCount { get; private set; }
        public bool ThrowOnFinalize { get; set; }

        public Task<CapturedState> CaptureStateAsync(
            PreparedWorld world,
            CancellationToken cancellationToken = default)
        {
            CaptureCount++;
            var package = Path.Combine(_root, $"candidate-{Guid.NewGuid():N}.package");
            File.Copy(Path.Combine(world.WorkingDirectory, "world-state.txt"), package);
            return Task.FromResult(new CapturedState(
                new StatePackage(Path.GetFileNameWithoutExtension(package), package),
                DateTimeOffset.UtcNow,
                DeletePackageAfterStore: true));
        }

        public Task FinalizePreparedWorldAsync(
            PreparedWorld world,
            PreparedWorldDisposition disposition,
            CancellationToken cancellationToken = default)
        {
            FinalizeCount++;
            Assert.Equal(PreparedWorldDisposition.Discard, disposition);
            if (ThrowOnFinalize)
            {
                throw new IOException("Injected cleanup failure.");
            }

            if (Directory.Exists(world.WorkingDirectory))
            {
                Directory.Delete(world.WorkingDirectory, recursive: true);
            }

            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<GameInstallation>> DiscoverInstallationsAsync(
            CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<GameInstallation>>([Installation]);

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

        public Task<PreparedWorld> PrepareEnvironmentAsync(
            GameInstallation installation,
            EnvironmentManifest requiredEnvironment,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task RestoreStateAsync(
            PreparedWorld world,
            StatePackage state,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<GameSessionHandle> LaunchLocalAsync(
            PreparedWorld world,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<GameSessionHandle> LaunchHostAsync(
            PreparedWorld world,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<GameSessionHandle> LaunchClientAsync(
            PreparedWorld world,
            HostConnection host,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task WaitForSessionEndAsync(
            GameSessionHandle session,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }
}
