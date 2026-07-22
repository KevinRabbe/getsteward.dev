using System.Net;
using SharedWorlds.Core.Abstractions;
using SharedWorlds.Core.Domain;
using SharedWorlds.Infrastructure.Remote;

namespace SharedWorlds.Infrastructure.Tests.Remote;

public sealed class StewardWorldStorageRecoveryJournalTests
{
    [Fact]
    public async Task CandidateIdIsJournaledBeforeAnyUploadNetworkOutcome()
    {
        var worldId = WorldId.New();
        var baseState = RevisionId.New();
        var environmentId = RevisionId.New();
        var candidateId = RevisionId.New();
        var recovery = new RecoveryStore();
        recovery.Records.Add(RecoveryRecord(worldId, baseState));
        var uploadApi = new RecordingHandler(_ => throw new HttpRequestException("network down"));
        using var harness = new Harness(uploadApi, recovery);
        RegisterLease(harness.Registry, worldId, baseState, environmentId);

        await Assert.ThrowsAsync<HttpRequestException>(() => harness.Storage.StoreRevisionAsync(
            Candidate(worldId, candidateId, baseState),
            new MemoryStream([1, 2, 3], writable: false)));

        var record = Assert.Single(recovery.Records);
        Assert.Equal(candidateId, record.CandidateStateRevisionId);
        Assert.Single(uploadApi.Requests);
    }

    [Fact]
    public async Task MissingRecoveryJournalBlocksUploadBeforeNetworkIo()
    {
        var worldId = WorldId.New();
        var baseState = RevisionId.New();
        var environmentId = RevisionId.New();
        var uploadApi = new RecordingHandler(_ => throw new InvalidOperationException("Upload must not run."));
        using var harness = new Harness(uploadApi, new RecoveryStore());
        RegisterLease(harness.Registry, worldId, baseState, environmentId);

        var exception = await Assert.ThrowsAsync<StewardWorldStorageException>(() =>
            harness.Storage.StoreRevisionAsync(
                Candidate(worldId, RevisionId.New(), baseState),
                new MemoryStream([1], writable: false)));

        Assert.Equal("RecoveryJournalMissing", exception.Code);
        Assert.Empty(uploadApi.Requests);
    }

    [Fact]
    public async Task ExistingDifferentCandidateCannotBeReplaced()
    {
        var worldId = WorldId.New();
        var baseState = RevisionId.New();
        var environmentId = RevisionId.New();
        var existingCandidate = RevisionId.New();
        var recovery = new RecoveryStore();
        recovery.Records.Add(RecoveryRecord(worldId, baseState) with
        {
            CandidateStateRevisionId = existingCandidate
        });
        var uploadApi = new RecordingHandler(_ => throw new InvalidOperationException("Upload must not run."));
        using var harness = new Harness(uploadApi, recovery);
        RegisterLease(harness.Registry, worldId, baseState, environmentId);

        var exception = await Assert.ThrowsAsync<StewardWorldStorageException>(() =>
            harness.Storage.StoreRevisionAsync(
                Candidate(worldId, RevisionId.New(), baseState),
                new MemoryStream([1], writable: false)));

        Assert.Equal("RecoveryCandidateConflict", exception.Code);
        Assert.Equal(existingCandidate, Assert.Single(recovery.Records).CandidateStateRevisionId);
        Assert.Empty(uploadApi.Requests);
    }

    private static StateRevision Candidate(
        WorldId worldId,
        RevisionId candidateId,
        RevisionId baseState)
        => new(
            candidateId,
            worldId,
            baseState,
            DateTimeOffset.UtcNow,
            new UserIdentity("steam", "76561198000000001", "Tester"),
            "factorio",
            "candidate");

    private static WorkspaceRecoveryRecord RecoveryRecord(
        WorldId worldId,
        RevisionId baseState)
        => new(
            WorkspaceId.New(),
            worldId,
            baseState,
            "factorio",
            Path.Combine(Path.GetTempPath(), "steward-recovery-journal", Guid.NewGuid().ToString("N")),
            new UserIdentity("steam", "76561198000000001", "Tester"),
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            WorkspaceRecoveryStatus.Active);

    private static void RegisterLease(
        StewardWritableReservationRegistry registry,
        WorldId worldId,
        RevisionId baseState,
        RevisionId environmentId)
    {
        Assert.True(registry.TryRegister(
            new StewardWritableReservationLease(
                worldId,
                Guid.NewGuid(),
                1,
                "device-a",
                new StewardRemoteWorldHead(baseState, environmentId),
                "steam",
                "76561198000000001"),
            new CancellationTokenSource()));
    }

    private sealed class Harness : IDisposable
    {
        private readonly List<HttpClient> _clients = [];
        private readonly string _cacheRoot = Path.Combine(
            Path.GetTempPath(),
            "steward-recovery-journal-tests",
            Guid.NewGuid().ToString("N"));

        public Harness(RecordingHandler uploadApi, RecoveryStore recovery)
        {
            Registry = new StewardWritableReservationRegistry();
            var unusedApi = Track(Client(new RecordingHandler(_ =>
                throw new InvalidOperationException("Unexpected API request."))));
            var uploadHttp = Track(Client(uploadApi));
            var transferHttp = Track(new HttpClient(new RecordingHandler(_ =>
                throw new InvalidOperationException("Unexpected object-store request."))));
            var metadata = new StewardWorldMetadataClient(unusedApi);
            var packages = new StewardVerifiedPackageSource(
                new StewardPackageDownloadClient(unusedApi),
                new VerifiedPackageCache(
                    _cacheRoot,
                    transferHttp,
                    new VerifiedPackageCacheOptions(0, 64 * 1024)));
            var uploads = new StewardPackageUploadClient(uploadHttp, transferHttp);
            Storage = new StewardWorldStorage(
                metadata,
                packages,
                uploads,
                new StewardAuthorityClient(unusedApi),
                new StaticTokenProvider(),
                Registry,
                recovery);
        }

        public StewardWorldStorage Storage { get; }
        public StewardWritableReservationRegistry Registry { get; }

        public void Dispose()
        {
            Registry.Dispose();
            foreach (var client in _clients)
            {
                client.Dispose();
            }

            if (Directory.Exists(_cacheRoot))
            {
                Directory.Delete(_cacheRoot, recursive: true);
            }
        }

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

    private sealed class StaticTokenProvider : IStewardAccessTokenProvider
    {
        public Task<string> GetAccessTokenAsync(CancellationToken cancellationToken = default)
            => Task.FromResult("access-token");
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
}
