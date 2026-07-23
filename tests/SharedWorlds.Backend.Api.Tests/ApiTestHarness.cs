using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.TestHost;
using SharedWorlds.Backend.Api;
using SharedWorlds.Backend.Identity;
using SharedWorlds.Backend.Transfers;
using SharedWorlds.Backend.Worlds;
using SharedWorlds.Core.Domain;

namespace SharedWorlds.Backend.Api.Tests;

internal sealed class ApiTestHarness : IAsyncDisposable
{
    private readonly WebApplication _app;

    private ApiTestHarness(
        WebApplication app,
        HttpClient client,
        InMemoryWorldStore worldStore,
        InMemoryTransferStore transferStore,
        InMemoryObjectStore objectStore,
        InMemorySessionStore sessionStore,
        MutableSteamHandler steamHandler,
        TestClock clock)
    {
        _app = app;
        Client = client;
        WorldStore = worldStore;
        TransferStore = transferStore;
        ObjectStore = objectStore;
        SessionStore = sessionStore;
        SteamHandler = steamHandler;
        Clock = clock;
    }

    public HttpClient Client { get; }
    public InMemoryWorldStore WorldStore { get; }
    public InMemoryTransferStore TransferStore { get; }
    public InMemoryObjectStore ObjectStore { get; }
    public InMemorySessionStore SessionStore { get; }
    public MutableSteamHandler SteamHandler { get; }
    public TestClock Clock { get; }

    public static async Task<ApiTestHarness> CreateAsync()
    {
        var clock = new TestClock();
        var worldStore = new InMemoryWorldStore();
        var transferStore = new InMemoryTransferStore();
        var objectStore = new InMemoryObjectStore();
        var sessionStore = new InMemorySessionStore();
        var steamHandler = new MutableSteamHandler();

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton(new StewardSessionService(
            sessionStore,
            () => clock.Now,
            tokenGenerator: new DeterministicTokenGenerator()));
        builder.Services.AddSingleton(new SharedWorldMetadataService(
            worldStore,
            () => clock.Now));
        builder.Services.AddSingleton(new SharedRevisionMetadataService(worldStore, worldStore));
        builder.Services.AddSingleton<ISharedWorldMetadataStore>(worldStore);
        builder.Services.AddSingleton<ISharedPackageTransferStore>(transferStore);
        builder.Services.AddSingleton<IPrivateImmutableObjectStore>(objectStore);
        builder.Services.AddSingleton(services => new SharedPackageTransferService(
            worldStore,
            services.GetRequiredService<SharedRevisionMetadataService>(),
            transferStore,
            objectStore,
            () => clock.Now,
            new SharedPackageTransferOptions(
                maximumPackageBytes: 1024 * 1024,
                partSizeBytes: 4,
                transferLifetime: TimeSpan.FromHours(24),
                authorizationLifetime: TimeSpan.FromMinutes(15))));
        builder.Services.AddSingleton(new SteamWebApiTicketVerifier(
            new HttpClient(steamHandler),
            new SteamWebApiTicketVerifierOptions(
                appId: 480,
                publisherApiKey: "server-secret",
                identity: "steward")));

        var app = builder.Build();
        app.UseStewardApiProblemHandling();
        app.MapStewardApiV1();
        await app.StartAsync();
        return new ApiTestHarness(
            app,
            app.GetTestClient(),
            worldStore,
            transferStore,
            objectStore,
            sessionStore,
            steamHandler,
            clock);
    }

    public ValueTask DisposeAsync()
    {
        Client.Dispose();
        return _app.DisposeAsync();
    }

    public sealed class TestClock
    {
        public DateTimeOffset Now { get; set; } =
            new(2026, 7, 22, 9, 0, 0, TimeSpan.Zero);
    }

    public sealed class MutableSteamHandler : HttpMessageHandler
    {
        public ulong SteamId64 { get; set; } = 76561198000000001UL;
        public string Result { get; set; } = "OK";

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var json = Result == "OK"
                ? $$"""
                    { "response": { "params": { "result": "OK", "steamid": "{{SteamId64}}" } } }
                    """
                : $$"""
                    { "response": { "params": { "result": "{{Result}}" } } }
                    """;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            });
        }
    }

    public sealed class DeterministicTokenGenerator : IStewardSessionTokenGenerator
    {
        private int _next;

        public string CreateAccessToken() => $"access-{++_next}";
        public string CreateRefreshToken() => $"refresh-{++_next}";
    }

    public sealed class InMemorySessionStore : IStewardSessionStore
    {
        private readonly object _gate = new();
        private StewardSessionRecord? _session;
        private readonly Dictionary<string, StewardAccessCredentialRecord> _access = new(StringComparer.Ordinal);

        public Task ReplaceInstallationSessionAsync(
            StewardSessionRecord session,
            StewardAccessCredentialRecord accessCredential,
            DateTimeOffset replacedAt,
            CancellationToken cancellationToken = default)
        {
            lock (_gate)
            {
                _session = session;
                _access.Clear();
                _access[accessCredential.AccessTokenHash] = accessCredential;
                return Task.CompletedTask;
            }
        }

        public Task<StewardAccessContext?> LoadAccessContextAsync(
            string accessTokenHash,
            CancellationToken cancellationToken = default)
        {
            lock (_gate)
            {
                if (_session is null || !_access.TryGetValue(accessTokenHash, out var access))
                {
                    return Task.FromResult<StewardAccessContext?>(null);
                }

                return Task.FromResult<StewardAccessContext?>(new(_session, access));
            }
        }

        public Task<StewardSessionRecord?> LoadRefreshSessionAsync(
            string refreshTokenHash,
            CancellationToken cancellationToken = default)
        {
            lock (_gate)
            {
                return Task.FromResult(
                    _session is not null &&
                    string.Equals(_session.RefreshTokenHash, refreshTokenHash, StringComparison.Ordinal)
                        ? _session
                        : null);
            }
        }

        public Task<StoreRotateRefreshSessionStatus> TryRotateRefreshSessionAsync(
            StewardSessionId sessionId,
            string expectedRefreshTokenHash,
            string installationId,
            string newRefreshTokenHash,
            DateTimeOffset newRefreshExpiresAt,
            StewardAccessCredentialRecord newAccessCredential,
            DateTimeOffset rotatedAt,
            CancellationToken cancellationToken = default)
        {
            lock (_gate)
            {
                if (_session is null ||
                    _session.Id != sessionId ||
                    _session.Revoked ||
                    !string.Equals(_session.RefreshTokenHash, expectedRefreshTokenHash, StringComparison.Ordinal) ||
                    !string.Equals(_session.InstallationId, installationId, StringComparison.Ordinal))
                {
                    return Task.FromResult(StoreRotateRefreshSessionStatus.InvalidCredential);
                }

                _session = _session with
                {
                    RefreshTokenHash = newRefreshTokenHash,
                    RefreshExpiresAt = newRefreshExpiresAt
                };
                _access.Clear();
                _access[newAccessCredential.AccessTokenHash] = newAccessCredential;
                return Task.FromResult(StoreRotateRefreshSessionStatus.Rotated);
            }
        }

        public Task<bool> TryRevokeRefreshSessionAsync(
            string refreshTokenHash,
            string installationId,
            DateTimeOffset revokedAt,
            CancellationToken cancellationToken = default)
        {
            lock (_gate)
            {
                if (_session is null ||
                    _session.Revoked ||
                    !string.Equals(_session.RefreshTokenHash, refreshTokenHash, StringComparison.Ordinal) ||
                    !string.Equals(_session.InstallationId, installationId, StringComparison.Ordinal))
                {
                    return Task.FromResult(false);
                }

                _session = _session with { Revoked = true, RevokedAt = revokedAt };
                _access.Clear();
                return Task.FromResult(true);
            }
        }
    }

    public sealed class InMemoryWorldStore : ISharedWorldMetadataStore, ISharedRevisionMetadataStore
    {
        private readonly object _gate = new();
        private readonly Dictionary<WorldId, SharedWorldMetadata> _worlds = [];
        private readonly Dictionary<(WorldId, ExternalIdentityRef), SharedWorldMember> _members = [];
        private readonly Dictionary<(WorldId, RevisionId), SharedStateRevisionMetadata> _states = [];
        private readonly Dictionary<(WorldId, RevisionId), SharedEnvironmentRevisionMetadata> _environments = [];

        public Task<bool> TryCreateWorldWithManagerAsync(
            SharedWorldMetadata world,
            SharedWorldMember accessManager,
            CancellationToken cancellationToken = default)
        {
            lock (_gate)
            {
                if (!_worlds.TryAdd(world.WorldId, world))
                {
                    return Task.FromResult(false);
                }

                _members[(world.WorldId, accessManager.Identity)] = accessManager;
                return Task.FromResult(true);
            }
        }

        public Task<SharedWorldMetadata?> LoadWorldAsync(
            WorldId worldId,
            CancellationToken cancellationToken = default)
        {
            lock (_gate)
            {
                _worlds.TryGetValue(worldId, out var world);
                return Task.FromResult(world);
            }
        }

        public Task<SharedWorldMember?> LoadMemberAsync(
            WorldId worldId,
            ExternalIdentityRef identity,
            CancellationToken cancellationToken = default)
        {
            lock (_gate)
            {
                _members.TryGetValue((worldId, identity), out var member);
                return Task.FromResult(member);
            }
        }

        public Task<IReadOnlyList<SharedWorldMetadata>> ListWorldsForActiveMemberAsync(
            ExternalIdentityRef identity,
            CancellationToken cancellationToken = default)
        {
            lock (_gate)
            {
                return Task.FromResult<IReadOnlyList<SharedWorldMetadata>>(
                    _members.Values
                        .Where(member => member.Identity == identity && member.Status == SharedWorldMemberStatus.Active)
                        .Select(member => _worlds[member.WorldId])
                        .ToArray());
            }
        }

        public Task<StoreRevisionMetadataStatus> TryRecordStateRevisionAsync(
            SharedStateRevisionMetadata revision,
            CancellationToken cancellationToken = default)
        {
            lock (_gate)
            {
                var key = (revision.WorldId, revision.RevisionId);
                if (_states.TryGetValue(key, out var existing))
                {
                    return Task.FromResult(existing == revision
                        ? StoreRevisionMetadataStatus.AlreadyRecorded
                        : StoreRevisionMetadataStatus.Conflict);
                }

                _states[key] = revision;
                return Task.FromResult(StoreRevisionMetadataStatus.Recorded);
            }
        }

        public Task<StoreRevisionMetadataStatus> TryRecordEnvironmentRevisionAsync(
            SharedEnvironmentRevisionMetadata revision,
            CancellationToken cancellationToken = default)
        {
            lock (_gate)
            {
                var key = (revision.WorldId, revision.RevisionId);
                if (_environments.TryGetValue(key, out var existing))
                {
                    return Task.FromResult(existing == revision
                        ? StoreRevisionMetadataStatus.AlreadyRecorded
                        : StoreRevisionMetadataStatus.Conflict);
                }

                _environments[key] = revision;
                return Task.FromResult(StoreRevisionMetadataStatus.Recorded);
            }
        }

        public Task<SharedStateRevisionMetadata?> LoadStateRevisionAsync(
            WorldId worldId,
            RevisionId revisionId,
            CancellationToken cancellationToken = default)
        {
            lock (_gate)
            {
                _states.TryGetValue((worldId, revisionId), out var revision);
                return Task.FromResult(revision);
            }
        }

        public Task<SharedEnvironmentRevisionMetadata?> LoadEnvironmentRevisionAsync(
            WorldId worldId,
            RevisionId revisionId,
            CancellationToken cancellationToken = default)
        {
            lock (_gate)
            {
                _environments.TryGetValue((worldId, revisionId), out var revision);
                return Task.FromResult(revision);
            }
        }
    }

    public sealed class InMemoryTransferStore : ISharedPackageTransferStore
    {
        private readonly object _gate = new();
        private readonly Dictionary<SharedPackageTransferId, SharedPackageTransferRecord> _records = [];

        public Task<bool> TryCreateAsync(
            SharedPackageTransferRecord transfer,
            CancellationToken cancellationToken = default)
        {
            lock (_gate)
            {
                if (_records.ContainsKey(transfer.Id) ||
                    _records.Values.Any(existing =>
                        string.Equals(existing.ObjectKey, transfer.ObjectKey, StringComparison.Ordinal) &&
                        existing.State is SharedPackageTransferState.Active or SharedPackageTransferState.Provisioning))
                {
                    return Task.FromResult(false);
                }

                _records[transfer.Id] = transfer;
                return Task.FromResult(true);
            }
        }

        public Task<SharedPackageTransferRecord?> LoadAsync(
            SharedPackageTransferId transferId,
            CancellationToken cancellationToken = default)
        {
            lock (_gate)
            {
                _records.TryGetValue(transferId, out var transfer);
                return Task.FromResult(transfer);
            }
        }

        public Task<SharedPackageTransferRecord?> LoadInFlightByObjectKeyAsync(
            string objectKey,
            CancellationToken cancellationToken = default)
        {
            lock (_gate)
            {
                return Task.FromResult(
                    _records.Values.SingleOrDefault(existing =>
                        string.Equals(existing.ObjectKey, objectKey, StringComparison.Ordinal) &&
                        existing.State is SharedPackageTransferState.Active or SharedPackageTransferState.Provisioning));
            }
        }

        public Task<bool> TryActivateProvisioningAsync(
            SharedPackageTransferId transferId,
            ExternalIdentityRef expectedOwner,
            string expectedPlaceholderProviderUploadId,
            string providerUploadId,
            CancellationToken cancellationToken = default)
        {
            lock (_gate)
            {
                if (!_records.TryGetValue(transferId, out var transfer) ||
                    transfer.Owner != expectedOwner ||
                    transfer.State != SharedPackageTransferState.Provisioning ||
                    !string.Equals(
                        transfer.ProviderUploadId,
                        expectedPlaceholderProviderUploadId,
                        StringComparison.Ordinal))
                {
                    return Task.FromResult(false);
                }

                _records[transferId] = transfer with
                {
                    ProviderUploadId = providerUploadId,
                    State = SharedPackageTransferState.Active
                };
                return Task.FromResult(true);
            }
        }

        public Task<bool> TrySetStateAsync(
            SharedPackageTransferId transferId,
            ExternalIdentityRef expectedOwner,
            SharedPackageTransferState expectedState,
            SharedPackageTransferState nextState,
            DateTimeOffset changedAt,
            CancellationToken cancellationToken = default)
        {
            lock (_gate)
            {
                if (!_records.TryGetValue(transferId, out var transfer) ||
                    transfer.Owner != expectedOwner ||
                    transfer.State != expectedState)
                {
                    return Task.FromResult(false);
                }

                _records[transferId] = transfer with
                {
                    State = nextState,
                    FinalizedAt = nextState == SharedPackageTransferState.Finalized
                        ? changedAt
                        : transfer.FinalizedAt
                };
                return Task.FromResult(true);
            }
        }
    }

    public sealed class InMemoryObjectStore : IPrivateImmutableObjectStore
    {
        private readonly object _gate = new();
        private readonly Dictionary<string, UploadState> _uploads = new(StringComparer.Ordinal);
        private readonly Dictionary<string, ImmutableStoredObject> _objects = new(StringComparer.Ordinal);
        private int _nextUpload;

        public Task<ImmutableUploadSession> BeginMultipartUploadAsync(
            string objectKey,
            long expectedByteSize,
            string expectedSha256,
            CancellationToken cancellationToken = default)
        {
            lock (_gate)
            {
                var id = $"upload-{++_nextUpload}";
                _uploads[id] = new UploadState(objectKey, expectedByteSize, expectedSha256);
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

                return Task.FromResult<ImmutableUploadSnapshot?>(new(
                    providerUploadId,
                    upload.ObjectKey,
                    Array.Empty<ImmutableUploadedPart>(),
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
                    throw new InvalidOperationException("Unknown test upload.");
                }

                return Task.FromResult(new DirectObjectTransferAuthorization(
                    new Uri($"https://storage.test/upload/{providerUploadId}/{partNumber}?signature=opaque"),
                    "PUT",
                    new Dictionary<string, string>(),
                    expiresAt,
                    expectedByteSize));
            }
        }

        public Task<ImmutableStoredObject> CompleteMultipartUploadAsync(
            string providerUploadId,
            CancellationToken cancellationToken = default)
        {
            lock (_gate)
            {
                var upload = _uploads[providerUploadId];
                var stored = new ImmutableStoredObject(
                    upload.ObjectKey,
                    upload.ExpectedByteSize,
                    upload.ExpectedSha256.ToUpperInvariant());
                _objects[upload.ObjectKey] = stored;
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
                return Task.CompletedTask;
            }
        }

        public Task<ImmutableStoredObject?> InspectObjectAsync(
            string objectKey,
            CancellationToken cancellationToken = default)
        {
            lock (_gate)
            {
                return Task.FromResult(_objects.GetValueOrDefault(objectKey));
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
                if (!_objects.ContainsKey(objectKey))
                {
                    throw new InvalidOperationException("Unknown test object.");
                }

                return Task.FromResult(new DirectObjectTransferAuthorization(
                    new Uri("https://storage.test/download?signature=opaque"),
                    "GET",
                    new Dictionary<string, string>(),
                    expiresAt,
                    expectedByteSize));
            }
        }

        public Task DeleteObjectAsync(
            string objectKey,
            CancellationToken cancellationToken = default)
        {
            lock (_gate)
            {
                _objects.Remove(objectKey);
                return Task.CompletedTask;
            }
        }

        private sealed record UploadState(
            string ObjectKey,
            long ExpectedByteSize,
            string ExpectedSha256,
            bool Completed = false);
    }
}
