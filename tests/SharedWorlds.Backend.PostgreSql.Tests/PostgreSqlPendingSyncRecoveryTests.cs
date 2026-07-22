using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using SharedWorlds.Backend.Api;
using SharedWorlds.Backend.Identity;
using SharedWorlds.Backend.PostgreSql;
using SharedWorlds.Backend.Transfers;
using SharedWorlds.Backend.Worlds;
using SharedWorlds.Core.Abstractions;
using SharedWorlds.Core.Domain;
using SharedWorlds.Core.Environment;
using SharedWorlds.Core.Worlds;
using SharedWorlds.Infrastructure.Remote;
using Xunit;

namespace SharedWorlds.Backend.PostgreSql.Tests;

public sealed class PostgreSqlPendingSyncRecoveryTests : IAsyncLifetime
{
    private NpgsqlDataSource _dataSource = null!;

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
    }

    public async Task DisposeAsync() => await _dataSource.DisposeAsync();

    [Fact]
    public async Task LostSuccessfulCommitResponseConvergesFromRecoveryJournalWithoutNewRevision()
    {
        var utcNow = new Func<DateTimeOffset>(() => DateTimeOffset.UtcNow);
        var worldStore = new PostgreSqlSharedWorldStore(_dataSource);
        var worlds = new SharedWorldMetadataService(worldStore, utcNow);
        var revisions = new SharedRevisionMetadataService(worldStore, worldStore);
        var authorityBase = new PostgreSqlSharedWorldAuthorityStore(_dataSource);
        var authorityStore = new PostgreSqlIdempotentReservationAuthorityStore(
            _dataSource,
            new PostgreSqlIdempotentSharedWorldAuthorityStore(_dataSource, authorityBase));
        var authority = new SharedWorldAuthorityService(authorityStore, utcNow);
        var abandon = new SharedWorldReservationAbandonService(
            new PostgreSqlSharedWorldReservationAbandonStore(_dataSource));
        var sessions = new StewardSessionService(
            new PostgreSqlStewardSessionStore(_dataSource),
            utcNow);
        var objectStore = new ByteObjectStore();
        var transfers = new SharedPackageTransferService(
            worldStore,
            revisions,
            new PostgreSqlSharedPackageTransferStore(_dataSource),
            objectStore,
            utcNow);

        var identity = new VerifiedExternalIdentity(
            new ExternalIdentityRef("steam", "76561198000000001"));
        var user = new UserIdentity("steam", identity.Subject.ExternalId, "Tester");
        var worldId = WorldId.New();
        var environmentId = RevisionId.New();
        var baseStateId = RevisionId.New();
        var manifest = new EnvironmentManifest(
            1,
            "factorio",
            "2.0.0",
            [],
            new Dictionary<string, string> { ["test"] = "recovery" });

        Assert.Equal(
            CreateSharedWorldStatus.Created,
            (await worlds.CreateSharedWorldAsync(
                identity,
                new CreateSharedWorldCommand(
                    worldId,
                    "factorio",
                    "Recovery World",
                    baseStateId,
                    environmentId))).Status);
        Assert.Equal(
            PublishEnvironmentManifestStatus.Published,
            await revisions.PublishEnvironmentManifestAsync(
                identity,
                worldId,
                environmentId,
                manifest));

        var baseBytes = Encoding.UTF8.GetBytes("N");
        var baseObject = objectStore.Seed(
            $"seed/{worldId.Value:N}/{baseStateId.Value:N}.package",
            baseBytes);
        Assert.Equal(
            RecordRevisionMetadataStatus.Recorded,
            await revisions.RecordVerifiedStateRevisionAsync(
                new SharedStateRevisionMetadata(
                    worldId,
                    baseStateId,
                    "factorio",
                    baseObject.ObjectKey,
                    baseObject.ByteSize,
                    baseObject.Sha256,
                    environmentId,
                    identity.Subject,
                    utcNow())));

        var tokens = await sessions.CreateSessionAsync(identity, "device-a");
        await using var app = await StartApiAsync(
            sessions,
            worlds,
            revisions,
            transfers,
            authority,
            abandon);

        var inner = app.GetTestServer().CreateHandler();
        using var droppingHandler = new DropFirstCommitResponseHandler(inner);
        using var apiClient = new HttpClient(droppingHandler)
        {
            BaseAddress = new Uri("http://localhost")
        };
        using var objectHttp = new HttpClient(new ByteObjectStoreHttpHandler(objectStore));
        var tokenProvider = new StaticTokenProvider(tokens.AccessToken);
        var metadata = new StewardWorldMetadataClient(apiClient);
        var authorityClient = new StewardAuthorityClient(apiClient);
        var abandonClient = new StewardReservationAbandonClient(apiClient);
        var recoveryStore = new RecoveryStore();
        using var registry = new StewardWritableReservationRegistry();
        var coordinator = new StewardWorldSessionCoordinator(
            metadata,
            authorityClient,
            abandonClient,
            tokenProvider,
            recoveryStore,
            registry,
            "device-a",
            new StewardWorldSessionCoordinatorOptions(
                TimeSpan.FromHours(1),
                acquireTransportAttempts: 1,
                headRefreshAttempts: 1));
        var cacheRoot = Path.Combine(
            Path.GetTempPath(),
            "steward-pending-sync-pg",
            Guid.NewGuid().ToString("N"));
        try
        {
            var storage = new StewardWorldStorage(
                metadata,
                new StewardVerifiedPackageSource(
                    new StewardPackageDownloadClient(apiClient),
                    new VerifiedPackageCache(
                        cacheRoot,
                        objectHttp,
                        new VerifiedPackageCacheOptions(0, 64 * 1024))),
                new StewardPackageUploadClient(apiClient, objectHttp),
                authorityClient,
                tokenProvider,
                registry,
                recoveryStore,
                new StewardWorldStorageOptions(commitTransportAttempts: 1));
            var gate = new ManagedWritableSessionGate();
            var lifecycle = new WorldLifecycleService(
                storage,
                coordinator,
                recoveryStore,
                gate);
            var adapter = new RecoveryLifecycleAdapter(cacheRoot);

            var exception = await Assert.ThrowsAsync<StewardCommitOutcomeUnknownException>(() =>
                lifecycle.ContinueLocalAsync(
                    worldId,
                    adapter,
                    adapter.Installation,
                    user));

            var pending = Assert.Single(recoveryStore.Records);
            Assert.Equal(WorkspaceRecoveryStatus.RecoveryPending, pending.Status);
            var candidateId = Assert.IsType<RevisionId>(pending.CandidateStateRevisionId);
            Assert.Equal(candidateId, exception.CandidateRevisionId);
            Assert.True(Directory.Exists(pending.WorkingDirectory));
            Assert.Equal(1, adapter.CaptureCount);
            Assert.NotNull(registry.Get(worldId));

            var canonicalAfterLostResponse = Assert.IsType<SharedWorldMetadata>(
                await worldStore.LoadWorldAsync(worldId));
            Assert.Equal(candidateId, canonicalAfterLostResponse.CurrentStateRevisionId);

            var countBeforeRecovery = await CountStateRevisionsAsync(worldId);
            Assert.Equal(2, countBeforeRecovery);

            var recovery = new StewardPendingSyncRecoveryService(
                storage,
                coordinator,
                abandonClient,
                tokenProvider,
                registry,
                recoveryStore,
                gate);
            var recovered = await recovery.RetryAsync(
                worldId,
                adapter,
                adapter.Installation,
                user);

            Assert.Equal(candidateId, recovered.CurrentStateRevisionId);
            Assert.Empty(recoveryStore.Records);
            Assert.Null(registry.Get(worldId));
            Assert.False(Directory.Exists(pending.WorkingDirectory));
            Assert.Equal(1, adapter.CaptureCount);
            Assert.Equal(countBeforeRecovery, await CountStateRevisionsAsync(worldId));
            Assert.Null(await authorityBase.GetReservationAsync(
                identity.Subject,
                worldId,
                utcNow(),
                SharedWorldAuthorityOptions.FirstReleaseDefaults));
        }
        finally
        {
            if (Directory.Exists(cacheRoot))
            {
                Directory.Delete(cacheRoot, recursive: true);
            }
        }
    }

    private async Task<int> CountStateRevisionsAsync(WorldId worldId)
    {
        await using var command = _dataSource.CreateCommand(
            "SELECT COUNT(*) FROM steward_state_revisions WHERE world_id = @world_id;");
        command.Parameters.AddWithValue("world_id", worldId.Value);
        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }

    private static async Task<WebApplication> StartApiAsync(
        StewardSessionService sessions,
        SharedWorldMetadataService worlds,
        SharedRevisionMetadataService revisions,
        SharedPackageTransferService transfers,
        SharedWorldAuthorityService authority,
        SharedWorldReservationAbandonService abandon)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = "Testing"
        });
        builder.WebHost.UseTestServer();
        builder.Services.ConfigureHttpJsonOptions(options =>
        {
            options.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
        });
        builder.Services.AddSingleton(sessions);
        builder.Services.AddSingleton(worlds);
        builder.Services.AddSingleton(revisions);
        builder.Services.AddSingleton(transfers);
        builder.Services.AddSingleton(authority);
        builder.Services.AddSingleton(abandon);
        builder.Services.AddSingleton(new SteamWebApiTicketVerifier(
            new HttpClient(new RejectingHandler()),
            new SteamWebApiTicketVerifierOptions(1, "unused", "unused")));

        var app = builder.Build();
        app.UseStewardApiProblemHandling();
        app.MapStewardApiV1();
        app.MapStewardRevisionMetadataApiV1();
        app.MapStewardAuthorityApiV1();
        app.MapStewardReservationAbandonApiV1();
        await app.StartAsync();
        return app;
    }

    private async Task ResetAsync()
    {
        await using var command = _dataSource.CreateCommand(
            "TRUNCATE TABLE steward_access_credentials, steward_auth_sessions, " +
            "steward_authority_idempotency, steward_object_cleanup_queue, " +
            "steward_world_reservations, steward_package_transfers, steward_world_invitations, " +
            "steward_state_revisions, steward_environment_revisions, steward_world_members, " +
            "steward_shared_worlds CASCADE;");
        await command.ExecuteNonQueryAsync();
    }

    private sealed class DropFirstCommitResponseHandler : DelegatingHandler
    {
        private int _dropRemaining = 1;

        public DropFirstCommitResponseHandler(HttpMessageHandler innerHandler)
            : base(innerHandler)
        {
        }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var response = await base.SendAsync(request, cancellationToken);
            if (request.RequestUri!.AbsolutePath.EndsWith("/reservation/commit", StringComparison.Ordinal) &&
                Interlocked.Exchange(ref _dropRemaining, 0) == 1)
            {
                response.Dispose();
                throw new HttpRequestException("Simulated lost successful commit response.");
            }

            return response;
        }
    }

    private sealed class RecoveryLifecycleAdapter : IGameAdapter
    {
        private readonly string _root;
        private string? _restored;

        public RecoveryLifecycleAdapter(string root)
        {
            _root = root;
            Installation = new GameInstallation("device-a", root, "test");
        }

        public string Id => "factorio";
        public string DisplayName => "Recovery Lifecycle Test";
        public GameAdapterCapabilities Capabilities => GameAdapterCapabilities.AutomaticLocalLaunch;
        public GameInstallation Installation { get; }
        public int CaptureCount { get; private set; }

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
        {
            var workspace = Path.Combine(_root, $"workspace-{Guid.NewGuid():N}");
            Directory.CreateDirectory(workspace);
            return Task.FromResult(new PreparedWorld(
                installation,
                workspace,
                requiredEnvironment,
                "Recovery World"));
        }

        public async Task RestoreStateAsync(
            PreparedWorld world,
            StatePackage state,
            CancellationToken cancellationToken = default)
        {
            _restored = await File.ReadAllTextAsync(state.Path, cancellationToken);
            await File.WriteAllTextAsync(
                Path.Combine(world.WorkingDirectory, "state.txt"),
                _restored,
                cancellationToken);
        }

        public Task<CapturedState> CaptureStateAsync(
            PreparedWorld world,
            CancellationToken cancellationToken = default)
        {
            CaptureCount++;
            var path = Path.Combine(_root, $"capture-{Guid.NewGuid():N}.package");
            File.WriteAllText(path, $"{_restored}|A");
            return Task.FromResult(new CapturedState(
                new StatePackage("candidate", path),
                DateTimeOffset.UtcNow,
                DeletePackageAfterStore: true));
        }

        public Task<GameSessionHandle> LaunchLocalAsync(
            PreparedWorld world,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new GameSessionHandle(1, DateTimeOffset.UtcNow));

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
            => Task.CompletedTask;

        public Task FinalizePreparedWorldAsync(
            PreparedWorld world,
            PreparedWorldDisposition disposition,
            CancellationToken cancellationToken = default)
        {
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
        private readonly string _token;

        public StaticTokenProvider(string token)
        {
            _token = token;
        }

        public Task<string> GetAccessTokenAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(_token);
    }

    private sealed class ByteObjectStore : IPrivateImmutableObjectStore
    {
        private readonly object _gate = new();
        private readonly Dictionary<string, Upload> _uploads = new(StringComparer.Ordinal);
        private readonly Dictionary<string, Stored> _objects = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _downloads = new(StringComparer.Ordinal);

        public ImmutableStoredObject Seed(string objectKey, byte[] bytes)
        {
            var metadata = Metadata(objectKey, bytes);
            lock (_gate)
            {
                _objects[objectKey] = new Stored(metadata, bytes.ToArray());
            }

            return metadata;
        }

        public Task<ImmutableUploadSession> BeginMultipartUploadAsync(
            string objectKey,
            long expectedByteSize,
            string expectedSha256,
            CancellationToken cancellationToken = default)
        {
            lock (_gate)
            {
                var existing = _uploads.Values.FirstOrDefault(upload =>
                    !upload.Completed && string.Equals(upload.ObjectKey, objectKey, StringComparison.Ordinal));
                if (existing is not null)
                {
                    return Task.FromResult(new ImmutableUploadSession(existing.Id, existing.ObjectKey));
                }

                var id = Guid.NewGuid().ToString("N");
                _uploads[id] = new Upload(id, objectKey, new SortedDictionary<int, byte[]>());
                return Task.FromResult(new ImmutableUploadSession(id, objectKey));
            }
        }

        public Task<ImmutableUploadSnapshot?> GetMultipartUploadAsync(
            string providerUploadId,
            CancellationToken cancellationToken = default)
        {
            lock (_gate)
            {
                if (!_uploads.TryGetValue(providerUploadId, out var upload))
                {
                    return Task.FromResult<ImmutableUploadSnapshot?>(null);
                }

                return Task.FromResult<ImmutableUploadSnapshot?>(new ImmutableUploadSnapshot(
                    providerUploadId,
                    upload.ObjectKey,
                    upload.Parts.Select(part => new ImmutableUploadedPart(part.Key, part.Value.LongLength)).ToArray(),
                    upload.Completed));
            }
        }

        public Task<DirectObjectTransferAuthorization> AuthorizeUploadPartAsync(
            string providerUploadId,
            int partNumber,
            long expectedByteSize,
            DateTimeOffset expiresAt,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new DirectObjectTransferAuthorization(
                new Uri($"https://storage.test/upload/{providerUploadId}/{partNumber}"),
                "PUT",
                new Dictionary<string, string>(),
                expiresAt,
                expectedByteSize));

        public Task<ImmutableStoredObject> CompleteMultipartUploadAsync(
            string providerUploadId,
            CancellationToken cancellationToken = default)
        {
            lock (_gate)
            {
                var upload = _uploads[providerUploadId];
                if (upload.Completed && _objects.TryGetValue(upload.ObjectKey, out var completed))
                {
                    return Task.FromResult(completed.Metadata);
                }

                var bytes = upload.Parts.OrderBy(part => part.Key).SelectMany(part => part.Value).ToArray();
                var metadata = Metadata(upload.ObjectKey, bytes);
                _objects[upload.ObjectKey] = new Stored(metadata, bytes);
                _uploads[providerUploadId] = upload with { Completed = true };
                return Task.FromResult(metadata);
            }
        }

        public Task AbortMultipartUploadAsync(string providerUploadId, CancellationToken cancellationToken = default)
        {
            lock (_gate)
            {
                _uploads.Remove(providerUploadId);
            }

            return Task.CompletedTask;
        }

        public Task<ImmutableStoredObject?> InspectObjectAsync(
            string objectKey,
            CancellationToken cancellationToken = default)
        {
            lock (_gate)
            {
                return Task.FromResult<ImmutableStoredObject?>(
                    _objects.TryGetValue(objectKey, out var stored) ? stored.Metadata : null);
            }
        }

        public Task<DirectObjectTransferAuthorization> AuthorizeDownloadAsync(
            string objectKey,
            long expectedByteSize,
            DateTimeOffset expiresAt,
            CancellationToken cancellationToken = default)
        {
            lock (_gate)
            {
                var token = Guid.NewGuid().ToString("N");
                _downloads[token] = objectKey;
                return Task.FromResult(new DirectObjectTransferAuthorization(
                    new Uri($"https://storage.test/download/{token}"),
                    "GET",
                    new Dictionary<string, string>(),
                    expiresAt,
                    expectedByteSize));
            }
        }

        public Task DeleteObjectAsync(string objectKey, CancellationToken cancellationToken = default)
        {
            lock (_gate)
            {
                _objects.Remove(objectKey);
            }

            return Task.CompletedTask;
        }

        public void Put(string uploadId, int partNumber, byte[] bytes)
        {
            lock (_gate)
            {
                _uploads[uploadId].Parts[partNumber] = bytes.ToArray();
            }
        }

        public byte[] Download(string token)
        {
            lock (_gate)
            {
                return _objects[_downloads[token]].Bytes.ToArray();
            }
        }

        private static ImmutableStoredObject Metadata(string key, byte[] bytes)
            => new(key, bytes.LongLength, Convert.ToHexString(SHA256.HashData(bytes)));

        private sealed record Stored(ImmutableStoredObject Metadata, byte[] Bytes);
        private sealed record Upload(
            string Id,
            string ObjectKey,
            SortedDictionary<int, byte[]> Parts,
            bool Completed = false);
    }

    private sealed class ByteObjectStoreHttpHandler : HttpMessageHandler
    {
        private readonly ByteObjectStore _store;

        public ByteObjectStoreHttpHandler(ByteObjectStore store)
        {
            _store = store;
        }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var segments = request.RequestUri!.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (request.Method == HttpMethod.Put &&
                segments is ["upload", var uploadId, var partText] &&
                int.TryParse(partText, out var partNumber))
            {
                _store.Put(
                    uploadId,
                    partNumber,
                    await request.Content!.ReadAsByteArrayAsync(cancellationToken));
                return new HttpResponseMessage(HttpStatusCode.OK);
            }

            if (request.Method == HttpMethod.Get && segments is ["download", var token])
            {
                var bytes = _store.Download(token);
                var from = request.Headers.Range?.Ranges.SingleOrDefault()?.From ?? 0;
                var payload = bytes[(int)from..];
                var response = new HttpResponseMessage(
                    from == 0 ? HttpStatusCode.OK : HttpStatusCode.PartialContent)
                {
                    Content = new ByteArrayContent(payload)
                };
                response.Content.Headers.ContentLength = payload.LongLength;
                if (from > 0)
                {
                    response.Content.Headers.ContentRange = new ContentRangeHeaderValue(
                        from,
                        bytes.LongLength - 1,
                        bytes.LongLength);
                }

                return response;
            }

            throw new InvalidOperationException($"Unexpected object-store request {request.Method} {request.RequestUri}.");
        }
    }

    private sealed class RejectingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => throw new InvalidOperationException("Steam authentication is not used by this test.");
    }
}
