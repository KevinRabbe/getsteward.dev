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

public sealed class PostgreSqlTwoDeviceHandoffTests : IAsyncLifetime
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
    public async Task PcAToPcBToPcAUsesVerifiedCanonicalHandoff()
    {
        var now = new Func<DateTimeOffset>(() => DateTimeOffset.UtcNow);
        var worldStore = new PostgreSqlSharedWorldStore(_dataSource);
        var worlds = new SharedWorldMetadataService(worldStore, now);
        var revisions = new SharedRevisionMetadataService(worldStore, worldStore);
        var accessStore = new PostgreSqlSharedWorldAccessStore(_dataSource);
        var authorityBase = new PostgreSqlSharedWorldAuthorityStore(_dataSource);
        var authorityCommitIdempotency = new PostgreSqlIdempotentSharedWorldAuthorityStore(
            _dataSource,
            authorityBase);
        var authorityStore = new PostgreSqlIdempotentReservationAuthorityStore(
            _dataSource,
            authorityCommitIdempotency);
        var authority = new SharedWorldAuthorityService(authorityStore, now);
        var abandon = new SharedWorldReservationAbandonService(
            new PostgreSqlSharedWorldReservationAbandonStore(_dataSource));
        var access = new SharedWorldAccessService(
            worldStore,
            accessStore,
            authorityBase,
            now);
        var sessions = new StewardSessionService(
            new PostgreSqlStewardSessionStore(_dataSource),
            now);
        var transferStore = new PostgreSqlSharedPackageTransferStore(_dataSource);
        var objectStore = new BytePreservingObjectStore();
        var transfers = new SharedPackageTransferService(
            worldStore,
            revisions,
            transferStore,
            objectStore,
            now);

        var identityA = Steam("76561198000000001");
        var identityB = Steam("76561198000000002");
        var userA = new UserIdentity("steam", identityA.Subject.ExternalId, "PC A");
        var userB = new UserIdentity("steam", identityB.Subject.ExternalId, "PC B");
        var worldId = WorldId.New();
        var environmentId = RevisionId.New();
        var initialStateId = RevisionId.New();
        var manifest = new EnvironmentManifest(
            1,
            "factorio",
            "2.0.0",
            [],
            new Dictionary<string, string> { ["test"] = "be5" });

        var created = await worlds.CreateSharedWorldAsync(
            identityA,
            new CreateSharedWorldCommand(
                worldId,
                "factorio",
                "BE-5 World",
                initialStateId,
                environmentId));
        Assert.Equal(CreateSharedWorldStatus.Created, created.Status);

        Assert.Equal(
            PublishEnvironmentManifestStatus.Published,
            await revisions.PublishEnvironmentManifestAsync(
                identityA,
                worldId,
                environmentId,
                manifest));

        var initialBytes = Encoding.UTF8.GetBytes("N");
        var initialStored = objectStore.Seed(
            $"seed/{worldId.Value:N}/{initialStateId.Value:N}.package",
            initialBytes);
        Assert.Equal(
            RecordRevisionMetadataStatus.Recorded,
            await revisions.RecordVerifiedStateRevisionAsync(
                new SharedStateRevisionMetadata(
                    worldId,
                    initialStateId,
                    "factorio",
                    initialStored.ObjectKey,
                    initialStored.ByteSize,
                    initialStored.Sha256,
                    environmentId,
                    identityA.Subject,
                    now())));

        var invitation = await access.CreateInvitationAsync(identityA, worldId, identityB.Subject);
        Assert.Equal(CreateWorldAccessInvitationStatus.Created, invitation.Status);
        Assert.NotNull(invitation.Invitation);
        Assert.Equal(
            RespondToWorldAccessInvitationStatus.Accepted,
            await access.AcceptInvitationAsync(identityB, invitation.Invitation.Id));

        var tokensA = await sessions.CreateSessionAsync(identityA, "device-a");
        var tokensB = await sessions.CreateSessionAsync(identityB, "device-b");

        await using var app = await StartApiAsync(
            sessions,
            worlds,
            revisions,
            transfers,
            authority,
            abandon);
        using var storageTransport = new ByteObjectStoreHttpHandler(objectStore);
        using var deviceA = new DeviceRuntime(
            app.GetTestClient(),
            storageTransport,
            tokensA.AccessToken,
            "device-a",
            "A",
            userA);
        using var deviceB = new DeviceRuntime(
            app.GetTestClient(),
            storageTransport,
            tokensB.AccessToken,
            "device-b",
            "B",
            userB);

        var nPlusOne = await deviceA.Lifecycle.ContinueLocalAsync(
            worldId,
            deviceA.Adapter,
            deviceA.Installation,
            userA);
        Assert.Equal("N", deviceA.Adapter.LastRestoredState);
        Assert.NotEqual(initialStateId, nPlusOne.CurrentStateRevisionId);
        var nPlusOneId = Assert.IsType<RevisionId>(nPlusOne.CurrentStateRevisionId);

        var nPlusTwo = await deviceB.Lifecycle.ContinueLocalAsync(
            worldId,
            deviceB.Adapter,
            deviceB.Installation,
            userB);
        Assert.Equal("N|A", deviceB.Adapter.LastRestoredState);
        Assert.NotEqual(nPlusOneId, nPlusTwo.CurrentStateRevisionId);
        var nPlusTwoId = Assert.IsType<RevisionId>(nPlusTwo.CurrentStateRevisionId);

        var canonical = Assert.IsType<SharedWorldMetadata>(await worldStore.LoadWorldAsync(worldId));
        Assert.Equal(nPlusTwoId, canonical.CurrentStateRevisionId);
        Assert.Equal(environmentId, canonical.CurrentEnvironmentRevisionId);

        var preparedOnA = await deviceA.Lifecycle.PrepareAsync(
            worldId,
            deviceA.Adapter,
            deviceA.Installation);
        Assert.Equal("N|A|B", deviceA.Adapter.LastRestoredState);
        Assert.Equal(nPlusTwoId, preparedOnA.World.CurrentStateRevisionId);
        await deviceA.Adapter.FinalizePreparedWorldAsync(
            preparedOnA.PreparedWorld,
            PreparedWorldDisposition.Discard);

        Assert.Null(await authorityBase.GetReservationAsync(
            identityA.Subject,
            worldId,
            now(),
            SharedWorldAuthorityOptions.FirstReleaseDefaults));
        Assert.Null(await authorityBase.GetReservationAsync(
            identityB.Subject,
            worldId,
            now(),
            SharedWorldAuthorityOptions.FirstReleaseDefaults));
    }

    private async Task<WebApplication> StartApiAsync(
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
            new HttpClient(new RejectingHttpHandler()),
            new SteamWebApiTicketVerifierOptions(1, "unused-test-key", "unused-test-identity")));

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

    private static VerifiedExternalIdentity Steam(string id)
        => new(new ExternalIdentityRef("steam", id));

    private sealed class DeviceRuntime : IDisposable
    {
        private readonly HttpClient _apiClient;
        private readonly HttpClient _transferClient;
        private readonly string _root;
        private readonly InMemoryRecoveryStore _recovery;
        private readonly StewardWritableReservationRegistry _reservations;

        public DeviceRuntime(
            HttpClient apiClient,
            HttpMessageHandler storageTransport,
            string accessToken,
            string installationId,
            string deviceTag,
            UserIdentity user)
        {
            _apiClient = apiClient;
            _transferClient = new HttpClient(storageTransport, disposeHandler: false);
            _root = Path.Combine(
                Path.GetTempPath(),
                "steward-be5",
                $"{deviceTag}-{Guid.NewGuid():N}");
            Directory.CreateDirectory(_root);

            var tokenProvider = new StaticTokenProvider(accessToken);
            var metadata = new StewardWorldMetadataClient(_apiClient);
            var authority = new StewardAuthorityClient(_apiClient);
            var abandon = new StewardReservationAbandonClient(_apiClient);
            var download = new StewardPackageDownloadClient(_apiClient);
            var cache = new VerifiedPackageCache(
                Path.Combine(_root, "cache"),
                _transferClient,
                new VerifiedPackageCacheOptions(0, 64 * 1024));
            var packages = new StewardVerifiedPackageSource(download, cache);
            var uploads = new StewardPackageUploadClient(_apiClient, _transferClient);

            _recovery = new InMemoryRecoveryStore();
            _reservations = new StewardWritableReservationRegistry();
            var coordinator = new StewardWorldSessionCoordinator(
                metadata,
                authority,
                abandon,
                tokenProvider,
                _recovery,
                _reservations,
                installationId,
                new StewardWorldSessionCoordinatorOptions(
                    TimeSpan.FromSeconds(30),
                    acquireTransportAttempts: 2,
                    headRefreshAttempts: 2));
            var storage = new StewardWorldStorage(
                metadata,
                packages,
                uploads,
                authority,
                tokenProvider,
                _reservations,
                new StewardWorldStorageOptions(commitTransportAttempts: 2));

            Adapter = new HandoffAdapter(_root, deviceTag);
            Installation = Adapter.Installation;
            Lifecycle = new WorldLifecycleService(storage, coordinator, _recovery);
            User = user;
        }

        public WorldLifecycleService Lifecycle { get; }
        public HandoffAdapter Adapter { get; }
        public GameInstallation Installation { get; }
        public UserIdentity User { get; }

        public void Dispose()
        {
            _reservations.Dispose();
            _transferClient.Dispose();
            _apiClient.Dispose();
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
    }

    private sealed class HandoffAdapter : IGameAdapter
    {
        private readonly string _root;
        private readonly string _deviceTag;
        private string? _restoredState;

        public HandoffAdapter(string root, string deviceTag)
        {
            _root = root;
            _deviceTag = deviceTag;
            Installation = new GameInstallation(
                $"installation-{deviceTag}",
                root,
                "test");
        }

        public string Id => "factorio";
        public string DisplayName => "BE-5 Test Adapter";
        public GameAdapterCapabilities Capabilities => GameAdapterCapabilities.AutomaticLocalLaunch;
        public GameInstallation Installation { get; }
        public string? LastRestoredState { get; private set; }

        public Task<IReadOnlyList<GameInstallation>> DiscoverInstallationsAsync(
            CancellationToken cancellationToken = default)
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
            var path = Path.Combine(_root, $"workspace-{Guid.NewGuid():N}");
            Directory.CreateDirectory(path);
            return Task.FromResult(new PreparedWorld(installation, path, requiredEnvironment));
        }

        public async Task RestoreStateAsync(
            PreparedWorld world,
            StatePackage state,
            CancellationToken cancellationToken = default)
        {
            var restored = await File.ReadAllTextAsync(state.Path, cancellationToken);
            _restoredState = restored;
            LastRestoredState = restored;
            await File.WriteAllTextAsync(
                Path.Combine(world.WorkingDirectory, "state.txt"),
                restored,
                cancellationToken);
        }

        public async Task<CapturedState> CaptureStateAsync(
            PreparedWorld world,
            CancellationToken cancellationToken = default)
        {
            var baseState = _restoredState
                ?? throw new InvalidOperationException("State must be restored before capture.");
            var next = $"{baseState}|{_deviceTag}";
            var packagePath = Path.Combine(_root, $"capture-{Guid.NewGuid():N}.package");
            await File.WriteAllTextAsync(packagePath, next, cancellationToken);
            return new CapturedState(
                new StatePackage(Path.GetFileNameWithoutExtension(packagePath), packagePath),
                DateTimeOffset.UtcNow,
                DeletePackageAfterStore: true);
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
            if (disposition == PreparedWorldDisposition.Discard &&
                Directory.Exists(world.WorkingDirectory))
            {
                Directory.Delete(world.WorkingDirectory, recursive: true);
            }

            return Task.CompletedTask;
        }
    }

    private sealed class InMemoryRecoveryStore : IWorkspaceRecoveryStore
    {
        private readonly Dictionary<WorkspaceId, WorkspaceRecoveryRecord> _records = [];

        public Task SaveAsync(
            WorkspaceRecoveryRecord record,
            CancellationToken cancellationToken = default)
        {
            _records[record.Id] = record;
            return Task.CompletedTask;
        }

        public Task RemoveAsync(
            WorkspaceId workspaceId,
            CancellationToken cancellationToken = default)
        {
            _records.Remove(workspaceId);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<WorkspaceRecoveryRecord>> ListAsync(
            CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<WorkspaceRecoveryRecord>>(_records.Values.ToArray());
    }

    private sealed class StaticTokenProvider : IStewardAccessTokenProvider
    {
        private readonly string _accessToken;

        public StaticTokenProvider(string accessToken)
        {
            _accessToken = accessToken;
        }

        public Task<string> GetAccessTokenAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(_accessToken);
    }

    private sealed class BytePreservingObjectStore : IPrivateImmutableObjectStore
    {
        private readonly object _gate = new();
        private readonly Dictionary<string, UploadState> _uploads = new(StringComparer.Ordinal);
        private readonly Dictionary<string, StoredState> _objects = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _downloadTokens = new(StringComparer.Ordinal);

        public ImmutableStoredObject Seed(string objectKey, byte[] bytes)
        {
            var stored = CreateStored(objectKey, bytes);
            lock (_gate)
            {
                _objects[objectKey] = new StoredState(stored, bytes.ToArray());
            }

            return stored;
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
                    !upload.Completed &&
                    string.Equals(upload.ObjectKey, objectKey, StringComparison.Ordinal));
                if (existing is not null)
                {
                    return Task.FromResult(new ImmutableUploadSession(existing.Id, existing.ObjectKey));
                }

                var id = Guid.NewGuid().ToString("N");
                _uploads[id] = new UploadState(
                    id,
                    objectKey,
                    expectedByteSize,
                    expectedSha256.ToUpperInvariant(),
                    new SortedDictionary<int, byte[]>());
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
        {
            lock (_gate)
            {
                if (!_uploads.ContainsKey(providerUploadId))
                {
                    throw new InvalidOperationException("Unknown upload.");
                }
            }

            return Task.FromResult(new DirectObjectTransferAuthorization(
                new Uri($"https://storage.test/upload/{providerUploadId}/{partNumber}"),
                "PUT",
                new Dictionary<string, string>(),
                expiresAt,
                expectedByteSize));
        }

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
                var stored = CreateStored(upload.ObjectKey, bytes);
                _objects[upload.ObjectKey] = new StoredState(stored, bytes);
                _uploads[providerUploadId] = upload with { Completed = true };
                return Task.FromResult(stored);
            }
        }

        public Task AbortMultipartUploadAsync(
            string providerUploadId,
            CancellationToken cancellationToken = default)
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
            string token;
            lock (_gate)
            {
                if (!_objects.ContainsKey(objectKey))
                {
                    throw new InvalidOperationException("Unknown object.");
                }

                token = Guid.NewGuid().ToString("N");
                _downloadTokens[token] = objectKey;
            }

            return Task.FromResult(new DirectObjectTransferAuthorization(
                new Uri($"https://storage.test/download/{token}"),
                "GET",
                new Dictionary<string, string>(),
                expiresAt,
                expectedByteSize));
        }

        public Task DeleteObjectAsync(
            string objectKey,
            CancellationToken cancellationToken = default)
        {
            lock (_gate)
            {
                _objects.Remove(objectKey);
            }

            return Task.CompletedTask;
        }

        public void PutPart(string providerUploadId, int partNumber, byte[] bytes)
        {
            lock (_gate)
            {
                var upload = _uploads[providerUploadId];
                upload.Parts[partNumber] = bytes.ToArray();
            }
        }

        public byte[] ReadDownload(string token)
        {
            lock (_gate)
            {
                var objectKey = _downloadTokens[token];
                return _objects[objectKey].Bytes.ToArray();
            }
        }

        private static ImmutableStoredObject CreateStored(string objectKey, byte[] bytes)
            => new(
                objectKey,
                bytes.LongLength,
                Convert.ToHexString(SHA256.HashData(bytes)));

        private sealed record StoredState(ImmutableStoredObject Metadata, byte[] Bytes);

        private sealed record UploadState(
            string Id,
            string ObjectKey,
            long ExpectedByteSize,
            string ExpectedSha256,
            SortedDictionary<int, byte[]> Parts,
            bool Completed = false);
    }

    private sealed class ByteObjectStoreHttpHandler : HttpMessageHandler
    {
        private readonly BytePreservingObjectStore _store;

        public ByteObjectStoreHttpHandler(BytePreservingObjectStore store)
        {
            _store = store;
        }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var segments = request.RequestUri!.AbsolutePath.Split(
                '/',
                StringSplitOptions.RemoveEmptyEntries);
            if (request.Method == HttpMethod.Put &&
                segments is ["upload", var providerUploadId, var partText] &&
                int.TryParse(partText, out var partNumber))
            {
                var bytes = await request.Content!.ReadAsByteArrayAsync(cancellationToken);
                _store.PutPart(providerUploadId, partNumber, bytes);
                return new HttpResponseMessage(HttpStatusCode.OK);
            }

            if (request.Method == HttpMethod.Get &&
                segments is ["download", var token])
            {
                var bytes = _store.ReadDownload(token);
                var from = request.Headers.Range?.Ranges.SingleOrDefault()?.From ?? 0;
                if (from < 0 || from > bytes.LongLength)
                {
                    return new HttpResponseMessage(HttpStatusCode.RequestedRangeNotSatisfiable);
                }

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

            throw new InvalidOperationException($"Unexpected object-store HTTP request: {request.Method} {request.RequestUri}.");
        }
    }

    private sealed class RejectingHttpHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => throw new InvalidOperationException("Steam verification is not used by this test.");
    }
}
