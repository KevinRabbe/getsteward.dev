using System.Net;
using System.Text;
using SharedWorlds.Core.Abstractions;
using SharedWorlds.Core.Domain;
using SharedWorlds.Core.Environment;
using SharedWorlds.Core.Worlds;
using SharedWorlds.Infrastructure.Remote;

namespace SharedWorlds.Infrastructure.Tests.Remote;

public sealed class StewardPendingSyncRecoveryServiceTests
{
    [Fact]
    public async Task CandidateAlreadyCanonicalFinalizesWithoutReacquiringAuthority()
    {
        using var root = new TemporaryDirectory();
        var worldId = WorldId.New();
        var baseState = RevisionId.New();
        var candidate = RevisionId.New();
        var environmentId = RevisionId.New();
        var user = User();
        var recovery = new RecoveryStore();
        recovery.Records.Add(RecoveryRecord(
            root.Path,
            worldId,
            baseState,
            candidate,
            environmentId,
            user));
        var storage = new FakeStorage(
            World(worldId, candidate, environmentId),
            Environment(worldId, environmentId));
        var coordinatorHarness = new CoordinatorHarness(
            worldId,
            baseState,
            environmentId,
            recovery,
            failOnAcquire: true);
        using var registry = coordinatorHarness.Registry;
        var adapter = new RecoveryAdapter(root.Path);
        var service = Service(storage, coordinatorHarness, recovery);

        var result = await service.RetryAsync(
            worldId,
            adapter,
            adapter.Installation,
            user);

        Assert.Equal(candidate, result.CurrentStateRevisionId);
        Assert.True(adapter.FinalizeCalled);
        Assert.Empty(recovery.Records);
        Assert.Empty(coordinatorHarness.AuthorityRequests);
    }

    [Fact]
    public async Task BaseHeadWithPublishedCandidateCommitsWithoutRecapture()
    {
        using var root = new TemporaryDirectory();
        var worldId = WorldId.New();
        var baseState = RevisionId.New();
        var candidate = RevisionId.New();
        var environmentId = RevisionId.New();
        var user = User();
        var recovery = new RecoveryStore();
        recovery.Records.Add(RecoveryRecord(
            root.Path,
            worldId,
            baseState,
            candidate,
            environmentId,
            user));
        var storage = new FakeStorage(
            World(worldId, baseState, environmentId),
            Environment(worldId, environmentId))
        {
            ExistingCandidate = new StateRevision(
                candidate,
                worldId,
                baseState,
                DateTimeOffset.UtcNow,
                user,
                "factorio",
                "candidate")
        };
        var coordinatorHarness = new CoordinatorHarness(
            worldId,
            baseState,
            environmentId,
            recovery);
        using var registry = coordinatorHarness.Registry;
        var adapter = new RecoveryAdapter(root.Path);
        var service = Service(storage, coordinatorHarness, recovery);

        var result = await service.RetryAsync(
            worldId,
            adapter,
            adapter.Installation,
            user);

        Assert.Equal(candidate, result.CurrentStateRevisionId);
        Assert.Equal(candidate, storage.SavedWorld!.CurrentStateRevisionId);
        Assert.Equal(0, adapter.CaptureCount);
        Assert.True(adapter.FinalizeCalled);
        Assert.Empty(recovery.Records);
        Assert.NotEmpty(coordinatorHarness.AuthorityRequests);
    }

    [Fact]
    public async Task UnpublishedCandidateIsRecapturedUsingSameJournaledRevisionId()
    {
        using var root = new TemporaryDirectory();
        var worldId = WorldId.New();
        var baseState = RevisionId.New();
        var candidate = RevisionId.New();
        var environmentId = RevisionId.New();
        var user = User();
        var recovery = new RecoveryStore();
        recovery.Records.Add(RecoveryRecord(
            root.Path,
            worldId,
            baseState,
            candidate,
            environmentId,
            user));
        var storage = new FakeStorage(
            World(worldId, baseState, environmentId),
            Environment(worldId, environmentId));
        var coordinatorHarness = new CoordinatorHarness(
            worldId,
            baseState,
            environmentId,
            recovery);
        using var registry = coordinatorHarness.Registry;
        var adapter = new RecoveryAdapter(root.Path);
        var service = Service(storage, coordinatorHarness, recovery);

        var result = await service.RetryAsync(
            worldId,
            adapter,
            adapter.Installation,
            user);

        Assert.Equal(candidate, result.CurrentStateRevisionId);
        Assert.Equal(1, adapter.CaptureCount);
        Assert.NotNull(storage.StoredRevision);
        Assert.Equal(candidate, storage.StoredRevision.Id);
        Assert.Equal(baseState, storage.StoredRevision.ParentRevisionId);
        Assert.Equal(candidate, storage.SavedWorld!.CurrentStateRevisionId);
        Assert.Empty(recovery.Records);
    }

    [Fact]
    public async Task DivergedCanonicalStateHeadIsNeverOverwritten()
    {
        using var root = new TemporaryDirectory();
        var worldId = WorldId.New();
        var baseState = RevisionId.New();
        var candidate = RevisionId.New();
        var newer = RevisionId.New();
        var environmentId = RevisionId.New();
        var user = User();
        var recovery = new RecoveryStore();
        recovery.Records.Add(RecoveryRecord(
            root.Path,
            worldId,
            baseState,
            candidate,
            environmentId,
            user));
        var storage = new FakeStorage(
            World(worldId, newer, environmentId),
            Environment(worldId, environmentId));
        var coordinatorHarness = new CoordinatorHarness(
            worldId,
            newer,
            environmentId,
            recovery,
            failOnAcquire: true);
        using var registry = coordinatorHarness.Registry;
        var adapter = new RecoveryAdapter(root.Path);
        var service = Service(storage, coordinatorHarness, recovery);

        var exception = await Assert.ThrowsAsync<StewardPendingSyncRecoveryException>(() =>
            service.RetryAsync(
                worldId,
                adapter,
                adapter.Installation,
                user));

        Assert.Equal("CanonicalHeadDiverged", exception.Code);
        Assert.Null(storage.SavedWorld);
        Assert.Single(recovery.Records);
        Assert.Empty(coordinatorHarness.AuthorityRequests);
    }

    [Fact]
    public async Task DivergedCanonicalEnvironmentIsNeverCombinedWithRecoveryWorkspace()
    {
        using var root = new TemporaryDirectory();
        var worldId = WorldId.New();
        var baseState = RevisionId.New();
        var candidate = RevisionId.New();
        var recordedEnvironment = RevisionId.New();
        var newerEnvironment = RevisionId.New();
        var user = User();
        var recovery = new RecoveryStore();
        recovery.Records.Add(RecoveryRecord(
            root.Path,
            worldId,
            baseState,
            candidate,
            recordedEnvironment,
            user));
        var storage = new FakeStorage(
            World(worldId, baseState, newerEnvironment),
            Environment(worldId, recordedEnvironment));
        var coordinatorHarness = new CoordinatorHarness(
            worldId,
            baseState,
            newerEnvironment,
            recovery,
            failOnAcquire: true);
        using var registry = coordinatorHarness.Registry;
        var adapter = new RecoveryAdapter(root.Path);
        var service = Service(storage, coordinatorHarness, recovery);

        var exception = await Assert.ThrowsAsync<StewardPendingSyncRecoveryException>(() =>
            service.RetryAsync(
                worldId,
                adapter,
                adapter.Installation,
                user));

        Assert.Equal("EnvironmentHeadDiverged", exception.Code);
        Assert.Equal(0, adapter.CaptureCount);
        Assert.Null(storage.SavedWorld);
        Assert.Single(recovery.Records);
        Assert.True(Directory.Exists(recovery.Records[0].WorkingDirectory));
        Assert.Empty(coordinatorHarness.AuthorityRequests);
    }

    [Fact]
    public async Task LegacyRecoveryWithWorkspaceButNoExactEnvironmentFailsClosed()
    {
        using var root = new TemporaryDirectory();
        var worldId = WorldId.New();
        var baseState = RevisionId.New();
        var candidate = RevisionId.New();
        var environmentId = RevisionId.New();
        var user = User();
        var recovery = new RecoveryStore();
        recovery.Records.Add(RecoveryRecord(
            root.Path,
            worldId,
            baseState,
            candidate,
            environmentId: null,
            user));
        var storage = new FakeStorage(
            World(worldId, baseState, environmentId),
            Environment(worldId, environmentId));
        var coordinatorHarness = new CoordinatorHarness(
            worldId,
            baseState,
            environmentId,
            recovery,
            failOnAcquire: true);
        using var registry = coordinatorHarness.Registry;
        var adapter = new RecoveryAdapter(root.Path);
        var service = Service(storage, coordinatorHarness, recovery);

        var exception = await Assert.ThrowsAsync<StewardPendingSyncRecoveryException>(() =>
            service.RetryAsync(
                worldId,
                adapter,
                adapter.Installation,
                user));

        Assert.Equal("EnvironmentUnknown", exception.Code);
        Assert.Equal(0, adapter.CaptureCount);
        Assert.Single(recovery.Records);
        Assert.True(Directory.Exists(recovery.Records[0].WorkingDirectory));
        Assert.Empty(coordinatorHarness.AuthorityRequests);
    }

    [Fact]
    public async Task PublishedCandidateWithWrongParentIsRejected()
    {
        using var root = new TemporaryDirectory();
        var worldId = WorldId.New();
        var baseState = RevisionId.New();
        var candidate = RevisionId.New();
        var environmentId = RevisionId.New();
        var user = User();
        var recovery = new RecoveryStore();
        recovery.Records.Add(RecoveryRecord(
            root.Path,
            worldId,
            baseState,
            candidate,
            environmentId,
            user));
        var storage = new FakeStorage(
            World(worldId, baseState, environmentId),
            Environment(worldId, environmentId))
        {
            ExistingCandidate = new StateRevision(
                candidate,
                worldId,
                RevisionId.New(),
                DateTimeOffset.UtcNow,
                user,
                "factorio",
                "candidate")
        };
        var coordinatorHarness = new CoordinatorHarness(
            worldId,
            baseState,
            environmentId,
            recovery);
        using var registry = coordinatorHarness.Registry;
        var adapter = new RecoveryAdapter(root.Path);
        var service = Service(storage, coordinatorHarness, recovery);

        var exception = await Assert.ThrowsAsync<StewardPendingSyncRecoveryException>(() =>
            service.RetryAsync(
                worldId,
                adapter,
                adapter.Installation,
                user));

        Assert.Equal("CandidateParentMismatch", exception.Code);
        Assert.Equal(0, adapter.CaptureCount);
        Assert.Null(storage.SavedWorld);
        Assert.Single(recovery.Records);
    }

    private static StewardPendingSyncRecoveryService Service(
        IWorldStorage storage,
        CoordinatorHarness coordinator,
        RecoveryStore recovery)
        => new(
            storage,
            coordinator.Coordinator,
            coordinator.Abandon,
            new StaticTokenProvider(),
            coordinator.Registry,
            recovery,
            new ManagedWritableSessionGate());

    private static WorkspaceRecoveryRecord RecoveryRecord(
        string root,
        WorldId worldId,
        RevisionId baseState,
        RevisionId candidate,
        RevisionId? environmentId,
        UserIdentity user)
    {
        var workspace = Path.Combine(root, "workspace");
        Directory.CreateDirectory(workspace);
        return new WorkspaceRecoveryRecord(
            WorkspaceId.New(),
            worldId,
            baseState,
            "factorio",
            workspace,
            user,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            WorkspaceRecoveryStatus.RecoveryPending,
            "waiting to sync",
            candidate,
            environmentId);
    }

    private static World World(
        WorldId worldId,
        RevisionId stateId,
        RevisionId environmentId)
        => new(
            worldId,
            "Factory",
            "factorio",
            [User()],
            environmentId,
            stateId)
        {
            SharingMode = WorldSharingMode.Shared
        };

    private static EnvironmentRevision Environment(
        WorldId worldId,
        RevisionId environmentId)
        => new(
            environmentId,
            worldId,
            null,
            DateTimeOffset.UtcNow,
            User(),
            new EnvironmentManifest(
                1,
                "factorio",
                "2.0.0",
                [],
                new Dictionary<string, string>()));

    private static UserIdentity User()
        => new("steam", "76561198000000001", "Tester");

    private sealed class CoordinatorHarness
    {
        private readonly List<HttpClient> _clients = [];

        public CoordinatorHarness(
            WorldId worldId,
            RevisionId stateId,
            RevisionId environmentId,
            RecoveryStore recovery,
            bool failOnAcquire = false)
        {
            Registry = new StewardWritableReservationRegistry();
            var metadata = new RecordingHandler(_ => JsonResponse(
                HttpStatusCode.OK,
                $$"""
                {
                  "code": "WorldFound",
                  "data": {
                    "worldId": "{{worldId.Value:D}}",
                    "adapterId": "factorio",
                    "displayName": "Factory",
                    "currentStateRevisionId": "{{stateId.Value:D}}",
                    "currentEnvironmentRevisionId": "{{environmentId.Value:D}}",
                    "accessManager": { "provider": "steam", "externalId": "76561198000000001" },
                    "createdAt": "2026-07-22T08:00:00Z",
                    "updatedAt": "2026-07-22T09:00:00Z"
                  },
                  "retryable": false
                }
                """));
            var authority = new RecordingHandler(request =>
            {
                if (request.RequestUri!.AbsolutePath.EndsWith("/reservation/acquire", StringComparison.Ordinal))
                {
                    if (failOnAcquire)
                    {
                        throw new InvalidOperationException("Acquire must not run.");
                    }

                    return JsonResponse(
                        HttpStatusCode.OK,
                        $$"""
                        {
                          "code": "ReservationAcquired",
                          "data": {
                            "worldId": "{{worldId.Value:D}}",
                            "sessionId": "{{Guid.NewGuid():D}}",
                            "generation": 3,
                            "holder": { "provider": "steam", "externalId": "76561198000000001" },
                            "installationId": "device-a",
                            "startingHead": {
                              "stateRevisionId": "{{stateId.Value:D}}",
                              "environmentRevisionId": "{{environmentId.Value:D}}"
                            },
                            "state": "Active",
                            "acquiredAt": "2026-07-22T09:00:00Z",
                            "lastHeartbeatAt": "2026-07-22T09:00:00Z",
                            "becameUncertainAt": null
                          },
                          "retryable": false
                        }
                        """);
                }

                if (request.RequestUri.AbsolutePath.EndsWith("/reservation/heartbeat", StringComparison.Ordinal))
                {
                    return JsonResponse(
                        HttpStatusCode.OK,
                        """{ "code": "HeartbeatAccepted", "retryable": false }""");
                }

                throw new InvalidOperationException($"Unexpected authority request {request.RequestUri}.");
            });
            AuthorityRequests = authority.Requests;
            var abandonHandler = new RecordingHandler(_ => JsonResponse(
                HttpStatusCode.OK,
                """{ "code": "ReservationAbandoned", "retryable": false }"""));

            var metadataClient = new StewardWorldMetadataClient(Track(Client(metadata)));
            Coordinator = new StewardWorldSessionCoordinator(
                metadataClient,
                new StewardAuthorityClient(Track(Client(authority))),
                new StewardReservationAbandonClient(Track(Client(abandonHandler))),
                new StaticTokenProvider(),
                recovery,
                Registry,
                "device-a",
                new StewardWorldSessionCoordinatorOptions(
                    TimeSpan.FromHours(1),
                    acquireTransportAttempts: 1,
                    headRefreshAttempts: 1));
            Abandon = new StewardReservationAbandonClient(Track(Client(abandonHandler)));
        }

        public StewardWorldSessionCoordinator Coordinator { get; }
        public StewardReservationAbandonClient Abandon { get; }
        public StewardWritableReservationRegistry Registry { get; }
        public List<HttpRequestMessage> AuthorityRequests { get; }

        private HttpClient Track(HttpClient client)
        {
            _clients.Add(client);
            return client;
        }

        private static HttpClient Client(HttpMessageHandler handler)
            => new(handler)
            {
                BaseAddress = new Uri("https://steward.test/")
            };
    }

    private sealed class FakeStorage : IWorldStorage
    {
        private World _world;
        private readonly EnvironmentRevision _environment;

        public FakeStorage(World world, EnvironmentRevision environment)
        {
            _world = world;
            _environment = environment;
        }

        public StateRevision? ExistingCandidate { get; set; }
        public StateRevision? StoredRevision { get; private set; }
        public World? SavedWorld { get; private set; }

        public Task<World?> LoadWorldAsync(
            WorldId worldId,
            CancellationToken cancellationToken = default)
            => Task.FromResult<World?>(_world.Id == worldId ? _world : null);

        public Task<IReadOnlyList<World>> ListWorldsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<World>>([_world]);

        public Task StoreEnvironmentRevisionAsync(
            EnvironmentRevision revision,
            CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task<EnvironmentRevision?> LoadEnvironmentRevisionAsync(
            WorldId worldId,
            RevisionId revisionId,
            CancellationToken cancellationToken = default)
            => Task.FromResult<EnvironmentRevision?>(
                _environment.WorldId == worldId && _environment.Id == revisionId
                    ? _environment
                    : null);

        public Task StoreRevisionAsync(
            StateRevision revision,
            Stream package,
            CancellationToken cancellationToken = default)
        {
            StoredRevision = revision;
            ExistingCandidate = revision;
            return Task.CompletedTask;
        }

        public Task<StateRevision?> LoadStateRevisionAsync(
            WorldId worldId,
            RevisionId revisionId,
            CancellationToken cancellationToken = default)
            => Task.FromResult(
                ExistingCandidate is { } candidate &&
                candidate.WorldId == worldId &&
                candidate.Id == revisionId
                    ? candidate
                    : null);

        public Task<Stream> OpenRevisionAsync(
            WorldId worldId,
            RevisionId revisionId,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task SaveWorldAsync(
            World world,
            CancellationToken cancellationToken = default)
        {
            SavedWorld = world;
            _world = world;
            return Task.CompletedTask;
        }
    }

    private sealed class RecoveryAdapter : IGameAdapter
    {
        private readonly string _root;

        public RecoveryAdapter(string root)
        {
            _root = root;
            Installation = new GameInstallation("test", root, "test");
        }

        public string Id => "factorio";
        public string DisplayName => "Recovery Test";
        public GameAdapterCapabilities Capabilities => GameAdapterCapabilities.AutomaticLocalLaunch;
        public GameInstallation Installation { get; }
        public int CaptureCount { get; private set; }
        public bool FinalizeCalled { get; private set; }

        public Task<IReadOnlyList<GameInstallation>> DiscoverInstallationsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<GameInstallation>>([Installation]);

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
        {
            CaptureCount++;
            var path = Path.Combine(_root, $"capture-{Guid.NewGuid():N}.package");
            File.WriteAllText(path, "recovered-candidate");
            return Task.FromResult(new CapturedState(
                new StatePackage("recovered", path),
                DateTimeOffset.UtcNow,
                DeletePackageAfterStore: true));
        }

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

        public Task FinalizePreparedWorldAsync(
            PreparedWorld world,
            PreparedWorldDisposition disposition,
            CancellationToken cancellationToken = default)
        {
            FinalizeCalled = true;
            if (disposition == PreparedWorldDisposition.Discard && Directory.Exists(world.WorkingDirectory))
            {
                Directory.Delete(world.WorkingDirectory, recursive: true);
            }

            return Task.CompletedTask;
        }
    }

    private sealed class RecoveryStore : IWorkspaceRecoveryStore
    {
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

    private sealed class StaticTokenProvider : IStewardAccessTokenProvider
    {
        public Task<string> GetAccessTokenAsync(CancellationToken cancellationToken = default)
            => Task.FromResult("access-token");
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responseFactory;

        public RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory)
        {
            _responseFactory = responseFactory;
        }

        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(_responseFactory(request));
        }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "steward-pending-sync-tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }

    private static HttpResponseMessage JsonResponse(HttpStatusCode statusCode, string json)
        => new(statusCode)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
}
