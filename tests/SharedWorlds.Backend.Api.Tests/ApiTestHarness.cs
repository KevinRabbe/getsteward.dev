using System.Text.Json.Serialization;
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
        VerifiedExternalIdentity identity,
        StewardSessionTokens tokens,
        WorldId worldId,
        RevisionId initialStateRevisionId,
        DateTimeOffset now)
    {
        _app = app;
        Client = client;
        Identity = identity;
        Tokens = tokens;
        WorldId = worldId;
        InitialStateRevisionId = initialStateRevisionId;
        Now = now;
    }

    public HttpClient Client { get; }
    public VerifiedExternalIdentity Identity { get; }
    public StewardSessionTokens Tokens { get; }
    public WorldId WorldId { get; }
    public RevisionId InitialStateRevisionId { get; }
    public DateTimeOffset Now { get; }

    public static async Task<ApiTestHarness> CreateAsync()
    {
        var now = new DateTimeOffset(2026, 7, 22, 12, 0, 0, TimeSpan.Zero);
        var identity = new VerifiedExternalIdentity(
            new ExternalIdentityRef("steam", "76561198000000001"));

        var sessionStore = new InMemorySessionStore();
        var sessionService = new StewardSessionService(sessionStore, () => now);
        var tokens = await sessionService.CreateSessionAsync(identity, "test-installation");

        var catalog = new InMemoryWorldCatalog();
        var worldService = new SharedWorldMetadataService(catalog, () => now);
        var revisionService = new SharedRevisionMetadataService(catalog, catalog);
        var transferStore = new InMemoryTransferStore();
        var objectStore = new InMemoryObjectStore();
        var transferService = new SharedPackageTransferService(
            catalog,
            revisionService,
            transferStore,
            objectStore,
            () => now);

        var worldId = new WorldId(Guid.NewGuid());
        var initialStateRevisionId = new RevisionId(Guid.NewGuid());
        var created = await worldService.CreateSharedWorldAsync(
            identity,
            new CreateSharedWorldCommand(
                worldId,
                "factorio",
                "HTTP Contract World",
                initialStateRevisionId,
                null));
        if (created.Status != CreateSharedWorldStatus.Created)
        {
            throw new InvalidOperationException("Test World could not be created.");
        }

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = "Testing"
        });
        builder.WebHost.UseTestServer();
        builder.Services.ConfigureHttpJsonOptions(options =>
        {
            options.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
        });
        builder.Services.AddSingleton(sessionService);
        builder.Services.AddSingleton(worldService);
        builder.Services.AddSingleton(revisionService);
        builder.Services.AddSingleton(transferService);
        builder.Services.AddSingleton<SteamWebApiTicketVerifier>(_ =>
            throw new InvalidOperationException("Steam verification must not be invoked by this API contract harness."));

        var app = builder.Build();
        app.UseStewardApiProblemHandling();
        app.MapStewardApiV1();
        await app.StartAsync();

        return new ApiTestHarness(
            app,
            app.GetTestClient(),
            identity,
            tokens,
            worldId,
            initialStateRevisionId,
            now);
    }

    public ValueTask DisposeAsync()
    {
        Client.Dispose();
        return _app.DisposeAsync();
    }

    private sealed class InMemorySessionStore : IStewardSessionStore
    {
        private readonly object _gate = new();
        private readonly Dictionary<StewardSessionId, StewardSessionRecord> _sessions = [];
        private readonly Dictionary<string, StewardSessionId> _refresh = new(StringComparer.Ordinal);
        private readonly Dictionary<string, StewardAccessCredentialRecord> _access = new(StringComparer.Ordinal);

        public Task ReplaceInstallationSessionAsync(
            StewardSessionRecord session,
            StewardAccessCredentialRecord accessCredential,
            DateTimeOffset replacedAt,
            CancellationToken cancellationToken = default)
        {
            lock (_gate)
            {
                foreach (var existing in _sessions.Values
                             .Where(value => !value.Revoked &&
                                             string.Equals(
                                                 value.InstallationId,
                                                 session.InstallationId,
                                                 StringComparison.Ordinal))
                             .ToArray())
                {
                    var revoked = existing with { Revoked = true, RevokedAt = replacedAt };
                    _sessions[existing.Id] = revoked;
                    _refresh.Remove(existing.RefreshTokenHash);
                    RemoveAccessForSession(existing.Id);
                }

                _sessions[session.Id] = session;
                _refresh[session.RefreshTokenHash] = session.Id;
                _access[accessCredential.AccessTokenHash] = accessCredential;
            }

            return Task.CompletedTask;
        }

        public Task<StewardAccessContext?> LoadAccessContextAsync(
            string accessTokenHash,
            CancellationToken cancellationToken = default)
        {
            lock (_gate)
            {
                if (!_access.TryGetValue(accessTokenHash, out var credential) ||
                    !_sessions.TryGetValue(credential.SessionId, out var session))
                {
                    return Task.FromResult<StewardAccessContext?>(null);
                }

                return Task.FromResult<StewardAccessContext?>(new(session, credential));
            }
        }

        public Task<StewardSessionRecord?> LoadRefreshSessionAsync(
            string refreshTokenHash,
            CancellationToken cancellationToken = default)
        {
            lock (_gate)
            {
                return Task.FromResult(
                    _refresh.TryGetValue(refreshTokenHash, out var sessionId) &&
                    _sessions.TryGetValue(sessionId, out var session)
                        ? session
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
                if (!_sessions.TryGetValue(sessionId, out var session) ||
                    session.Revoked ||
                    !string.Equals(session.RefreshTokenHash, expectedRefreshTokenHash, StringComparison.Ordinal) ||
                    !string.Equals(session.InstallationId, installationId, StringComparison.Ordinal))
                {
                    return Task.FromResult(StoreRotateRefreshSessionStatus.InvalidCredential);
                }

                _refresh.Remove(expectedRefreshTokenHash);
                RemoveAccessForSession(sessionId);
                var rotated = session with
                {
                    RefreshTokenHash = newRefreshTokenHash,
                    RefreshExpiresAt = newRefreshExpiresAt
                };
                _sessions[sessionId] = rotated;
                _refresh[newRefreshTokenHash] = sessionId;
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
                if (!_refresh.TryGetValue(refreshTokenHash, out var sessionId) ||
                    !_sessions.TryGetValue(sessionId, out var session) ||
                    session.Revoked ||
                    !string.Equals(session.InstallationId, installationId, StringComparison.Ordinal))
                {
                    return Task.FromResult(false);
                }

                _sessions[sessionId] = session with { Revoked = true, RevokedAt = revokedAt };
                _refresh.Remove(refreshTokenHash);
                RemoveAccessForSession(sessionId);
                return Task.FromResult(true);
            }
        }

        private void RemoveAccessForSession(StewardSessionId sessionId)
        {
            foreach (var key in _access
                         .Where(pair => pair.Value.SessionId == sessionId)
                         .Select(pair => pair.Key)
                         .ToArray())
            {
                _access.Remove(key);
            }
        }
    }

    private sealed class InMemoryWorldCatalog :
        ISharedWorldMetadataStore,
        ISharedRevisionMetadataStore
    {
        private readonly object _gate = new();
        private readonly Dictionary<WorldId, SharedWorldMetadata> _worlds = [];
        private readonly Dictionary<(WorldId WorldId, ExternalIdentityRef Identity), SharedWorldMember> _members = [];
        private readonly Dictionary<(WorldId WorldId, RevisionId RevisionId), SharedStateRevisionMetadata> _states = [];
        private readonly Dictionary<(WorldId WorldId, RevisionId RevisionId), SharedEnvironmentRevisionMetadata> _environments = [];

        public Task<bool> TryCreateWorldWithManagerAsync(
            SharedWorldMetadata world,
            SharedWorldMember accessManager,
            CancellationToken cancellationToken = default)
        {
            lock (_gate)
            {
                if (_worlds.ContainsKey(world.WorldId))
                {
                    return Task.FromResult(false);
                }

                _worlds[world.WorldId] = world;
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
                return Task.FromResult(_worlds.GetValueOrDefault(worldId));
            }
        }

        public Task<SharedWorldMember?> LoadMemberAsync(
            WorldId worldId,
            ExternalIdentityRef identity,
            CancellationToken cancellationToken = default)
        {
            lock (_gate)
            {
                return Task.FromResult(_members.GetValueOrDefault((worldId, identity)));
            }
        }

        public Task<IReadOnlyList<SharedWorldMetadata>> ListWorldsForActiveMemberAsync(
            ExternalIdentityRef identity,
            CancellationToken cancellationToken = default)
        {
            lock (_gate)
            {
                IReadOnlyList<SharedWorldMetadata> worlds = _members
                    .Where(pair => pair.Key.Identity == identity &&
                                   pair.Value.Status == SharedWorldMemberStatus.Active)
                    .Select(pair => _worlds[pair.Key.WorldId])
                    .OrderBy(world => world.DisplayName, StringComparer.Ordinal)
                    .ToArray();
                return Task.FromResult(worlds);
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
                return Task.FromResult(_states.GetValueOrDefault((worldId, revisionId)));
            }
        }

        public Task<SharedEnvironmentRevisionMetadata?> LoadEnvironmentRevisionAsync(
            WorldId worldId,
            RevisionId revisionId,
            CancellationToken cancellationToken = default)
        {
            lock (_gate)
            {
                return Task.FromResult(_environments.GetValueOrDefault((worldId, revisionId)));
            }
        }
    }

    private sealed class InMemoryTransferStore : ISharedPackageTransferStore
    {
        private readonly object _gate = new();
        private readonly Dictionary<SharedPackageTransferId, SharedPackageTransferRecord> _transfers = [];

        public Task<bool> TryCreateAsync(
            SharedPackageTransferRecord transfer,
            CancellationToken cancellationToken = default)
        {
            lock (_gate)
            {
                if (_transfers.ContainsKey(transfer.Id))
                {
                    return Task.FromResult(false);
                }

                _transfers[transfer.Id] = transfer;
                return Task.FromResult(true);
            }
        }

        public Task<SharedPackageTransferRecord?> LoadAsync(
            SharedPackageTransferId transferId,
            CancellationToken cancellationToken = default)
        {
            lock (_gate)
            {
                return Task.FromResult(_transfers.GetValueOrDefault(transferId));
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
                if (!_transfers.TryGetValue(transferId, out var current) ||
                    current.Owner != expectedOwner ||
                    current.State != expectedState)
                {
                    return Task.FromResult(false);
                }

                _transfers[transferId] = current with
                {
                    State = nextState,
                    FinalizedAt = nextState == SharedPackageTransferState.Finalized
                        ? changedAt
                        : current.FinalizedAt
                };
                return Task.FromResult(true);
            }
        }
    }

    private sealed class InMemoryObjectStore : IPrivateImmutableObjectStore
    {
        private readonly object _gate = new();
        private readonly Dictionary<string, UploadState> _uploads = new(StringComparer.Ordinal);
        private readonly Dictionary<string, ImmutableStoredObject> _objects = new(StringComparer.Ordinal);

        public Task<ImmutableUploadSession> BeginMultipartUploadAsync(
            string objectKey,
            long expectedByteSize,
            string expectedSha256,
            CancellationToken cancellationToken = default)
        {
            lock (_gate)
            {
                var handle = $"test-upload-{Guid.NewGuid():N}";
                _uploads[handle] = new UploadState(objectKey, expectedByteSize, expectedSha256);
                return Task.FromResult(new ImmutableUploadSession(handle, objectKey));
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
                    IsCompleted: upload.Completed));
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
                    new Uri($"https://storage.test/upload/{partNumber}?signature=opaque"),
                    "PUT",
                    new Dictionary<string, string>
                    {
                        ["Content-Length"] = expectedByteSize.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    },
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
                if (!_uploads.TryGetValue(providerUploadId, out var upload))
                {
                    throw new InvalidOperationException("Unknown test upload.");
                }

                var stored = new ImmutableStoredObject(
                    upload.ObjectKey,
                    upload.ExpectedByteSize,
                    upload.ExpectedSha256);
                _uploads[providerUploadId] = upload with { Completed = true };
                _objects[upload.ObjectKey] = stored;
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
