using System.Net;
using System.Security.Cryptography;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Npgsql;
using SharedWorlds.Backend.Identity;
using SharedWorlds.Backend.ObjectStorage.S3;
using SharedWorlds.Backend.PostgreSql;
using SharedWorlds.Backend.Transfers;
using SharedWorlds.Backend.Worlds;
using SharedWorlds.Core.Domain;
using SharedWorlds.Core.Environment;
using SharedWorlds.Core.Worlds;
using SharedWorlds.Infrastructure.Remote;
using SharedWorlds.Infrastructure.Storage;

namespace SharedWorlds.BringHere.EndToEndTests;

public sealed class PrivateBringHereEndToEndTests
{
    [Fact]
    public async Task SourcePublicationThroughPostgreSqlAndS3MaterializesExactTargetWorld()
    {
        var connectionString = RequireEnvironment("STEWARD_BRING_HERE_TEST_POSTGRES");
        var s3Endpoint = new Uri(RequireEnvironment("STEWARD_BRING_HERE_TEST_S3_ENDPOINT"));
        var s3Region = RequireEnvironment("STEWARD_BRING_HERE_TEST_S3_REGION");
        var s3AccessKey = RequireEnvironment("STEWARD_BRING_HERE_TEST_S3_ACCESS_KEY");
        var s3SecretKey = RequireEnvironment("STEWARD_BRING_HERE_TEST_S3_SECRET_KEY");
        var bucket = $"steward-bring-here-{Guid.NewGuid():N}";
        var root = Path.Combine(
            Path.GetTempPath(),
            $"steward-bring-here-e2e-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);

        await using var dataSource = new NpgsqlDataSourceBuilder(connectionString).Build();
        using var providerClient = CreateS3Client(
            s3Endpoint,
            s3Region,
            s3AccessKey,
            s3SecretKey);
        await providerClient.PutBucketAsync(new PutBucketRequest
        {
            BucketName = bucket
        });

        using var objectStore = S3CompatibleObjectStoreFactory.Create(
            new S3CompatibleObjectStoreOptions(
                s3Endpoint,
                s3Region,
                bucket,
                s3AccessKey,
                s3SecretKey,
                forcePathStyle: true));
        using var transferClient = new HttpClient(new HttpClientHandler
        {
            AllowAutoRedirect = false
        })
        {
            Timeout = Timeout.InfiniteTimeSpan
        };

        try
        {
            var locations = new PostgreSqlOwnedWorldLocationStore(dataSource);
            var persistedSnapshots = new PostgreSqlOwnedWorldSnapshotStore(dataSource);
            var snapshots = new PostgreSqlBringHereOwnedWorldSnapshotStore(
                dataSource,
                persistedSnapshots);
            var revisionEvidence =
                new PostgreSqlOwnedWorldSnapshotRevisionEvidenceStore(dataSource);
            var transfers = new PostgreSqlPrivateSnapshotTransferStore(dataSource);
            await locations.InitializeAsync();
            await persistedSnapshots.InitializeAsync();
            await revisionEvidence.InitializeAsync();
            await transfers.InitializeAsync();

            var now = DateTimeOffset.UtcNow;
            var identity = new VerifiedExternalIdentity(
                new ExternalIdentityRef("bring-here-e2e", "owner"),
                "Bring Here Owner");
            const string sourceInstallation = "source-installation-e2e";
            const string targetInstallation = "target-installation-e2e";
            var sourceCaller = new StewardAuthenticatedCaller(
                identity,
                sourceInstallation,
                new StewardSessionId(Guid.NewGuid()));
            var targetCaller = new StewardAuthenticatedCaller(
                identity,
                targetInstallation,
                new StewardSessionId(Guid.NewGuid()));
            var locationService = new OwnedWorldLocationApplicationService(
                locations,
                () => now);
            await locationService.RegisterCurrentInstallationAsync(
                sourceCaller,
                "Source PC");
            await locationService.RegisterCurrentInstallationAsync(
                targetCaller,
                "Target PC");

            var owner = new UserIdentity(
                identity.Subject.Provider,
                identity.Subject.ExternalId,
                identity.DisplayName);
            var worldId = WorldId.New();
            var environmentId = RevisionId.New();
            var stateId = RevisionId.New();
            const string adapterId = "factorio";
            const string worldName = "Disposable Bring Here World";
            var environment = new EnvironmentRevision(
                environmentId,
                worldId,
                ParentRevisionId: null,
                now,
                owner,
                new EnvironmentManifest(
                    SchemaVersion: 1,
                    AdapterId: adapterId,
                    GameVersion: "2.1.11",
                    Components: [],
                    Configuration: new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["mode"] = "vanilla"
                    }));
            var state = new StateRevision(
                stateId,
                worldId,
                ParentRevisionId: null,
                now,
                owner,
                adapterId,
                "disposable-state-package",
                environmentId);
            var sourceWorld = new World(
                worldId,
                worldName,
                adapterId,
                Members: [owner],
                CurrentEnvironmentRevisionId: environmentId,
                CurrentStateRevisionId: stateId);
            var payload = CreatePayload(1024 * 1024 + 137);
            var payloadSha256 = Convert.ToHexString(SHA256.HashData(payload));

            var sourceStorage = new LocalWorldStorage(Path.Combine(root, "source"));
            await sourceStorage.StoreEnvironmentRevisionAsync(environment);
            await using (var package = new MemoryStream(payload, writable: false))
            {
                await sourceStorage.StoreRevisionAsync(state, package);
            }

            await sourceStorage.SaveWorldAsync(sourceWorld);
            var sourcePublication = await locationService
                .PublishCurrentLocationWithPresentationAsync(
                    sourceCaller,
                    worldId,
                    stateId,
                    environmentId,
                    worldName,
                    adapterId);
            Assert.Equal(
                OwnedWorldLocationWriteResult.Created,
                sourcePublication.Result);

            var transferOptions = new PrivateSnapshotTransferOptions(
                maximumPackageBytes: 8 * 1024 * 1024,
                partSizeBytes: 5 * 1024 * 1024,
                transferLifetime: TimeSpan.FromHours(1),
                authorizationLifetime: TimeSpan.FromMinutes(10));
            var transferService = new PrivateSnapshotTransferService(
                locations,
                snapshots,
                transfers,
                objectStore,
                () => now,
                transferOptions);
            var begin = await transferService.BeginUploadAsync(
                sourceCaller,
                new BeginPrivateSnapshotUploadCommand(
                    worldId,
                    stateId,
                    environmentId,
                    adapterId,
                    payload.LongLength,
                    payloadSha256,
                    environment.Manifest));
            Assert.Equal(BeginPrivateSnapshotUploadStatus.Started, begin.Status);
            var transfer = Assert.IsType<PrivateSnapshotTransferRecord>(begin.Transfer);
            Assert.Equal(1, transfer.PartCount);

            var part = await transferService.AuthorizePartAsync(
                sourceCaller,
                transfer.Id,
                partNumber: 1);
            Assert.Equal(AuthorizePrivateSnapshotPartStatus.Authorized, part.Status);
            await UploadPartAsync(
                transferClient,
                Assert.IsType<DirectObjectTransferAuthorization>(part.Authorization),
                payload);

            var finalized = await transferService.FinalizeAsync(
                sourceCaller,
                transfer.Id);
            Assert.Equal(FinalizePrivateSnapshotUploadStatus.Finalized, finalized.Status);
            var snapshot = Assert.IsType<OwnedWorldSnapshot>(finalized.Snapshot);
            Assert.Equal(worldId, snapshot.WorldId);
            Assert.Equal(sourceInstallation, snapshot.InstallationId);
            Assert.Equal(stateId, snapshot.StateRevisionId);
            Assert.Equal(environmentId, snapshot.EnvironmentRevisionId);
            Assert.Equal(payload.LongLength, snapshot.StatePackageByteSize);
            Assert.Equal(payloadSha256, snapshot.StatePackageSha256, ignoreCase: true);

            var evidenceService = new PrivateSnapshotRevisionEvidenceService(
                snapshots,
                revisionEvidence,
                () => now);
            var evidence = await evidenceService.PublishAsync(
                sourceCaller,
                new PublishPrivateSnapshotRevisionEvidenceCommand(
                    worldId,
                    state,
                    environment));
            Assert.Equal(
                PublishPrivateSnapshotRevisionEvidenceStatus.Published,
                evidence.Status);

            var materializationDownload =
                new PrivateSnapshotMaterializationDownloadService(
                    locations,
                    snapshots,
                    revisionEvidence,
                    objectStore,
                    () => now,
                    transferOptions);
            var authorized = await materializationDownload.AuthorizeAsync(
                targetCaller,
                worldId);
            Assert.Equal(
                AuthorizePrivateSnapshotDownloadStatus.Authorized,
                authorized.Status);
            var backendPlan = Assert.IsType<PrivateSnapshotMaterializationDownloadPlan>(
                authorized.Plan);
            Assert.Equal(sourceInstallation, backendPlan.SourceInstallationId);
            Assert.Equal(state, backendPlan.StateRevision);
            Assert.Equal(environment.Id, backendPlan.EnvironmentRevision.Id);

            var download = new AuthorizedPackageDownload(
                backendPlan.Authorization.Uri,
                backendPlan.Authorization.RequiredHeaders,
                backendPlan.Authorization.ExpiresAt,
                backendPlan.ExpectedByteSize,
                backendPlan.ExpectedSha256);
            var cache = new VerifiedPackageCache(
                Path.Combine(root, "target-cache"),
                transferClient,
                new VerifiedPackageCacheOptions(
                    minimumFreeSpaceReserveBytes: 0,
                    copyBufferBytes: 64 * 1024,
                    maximumCacheBytes: 16 * 1024 * 1024,
                    transferInactivityTimeout: TimeSpan.FromSeconds(30)));
            var cached = await cache.EnsureAsync(download);
            Assert.Equal(payload.LongLength, cached.ByteSize);
            Assert.Equal(payloadSha256, cached.Sha256, ignoreCase: true);

            var remotePlan = new RemotePrivateSnapshotMaterializationPlan(
                backendPlan.WorldId,
                backendPlan.SourceInstallationId,
                backendPlan.StateRevisionId,
                backendPlan.EnvironmentRevisionId,
                backendPlan.GameAdapterId,
                backendPlan.ExpectedByteSize,
                backendPlan.ExpectedSha256,
                backendPlan.EnvironmentManifest,
                backendPlan.StateRevision,
                backendPlan.EnvironmentRevision,
                download);
            var prepared = new RemoteVerifiedPrivateSnapshotMaterialization(
                remotePlan,
                cached);
            var sourceClaim = Assert.Single(await locations.ListWorldLocationsAsync(
                worldId,
                owner.Provider,
                owner.ExternalId));
            var clientSource = new StewardOwnedWorldLocation(
                worldId,
                sourceClaim.InstallationId,
                sourceClaim.StateRevisionId,
                sourceClaim.EnvironmentRevisionId,
                sourceClaim.ObservedAt,
                new StewardOwnedWorldPresentation(worldName, adapterId));
            var catalogEntry = new StewardOwnedPrivateWorldCatalogEntry(
                worldId,
                worldName,
                adapterId,
                BringHereAvailability.Available,
                clientSource,
                ConflictingClaims: [],
                "One exact remote private snapshot is ready for materialization.");

            var targetStorage = new LocalWorldStorage(Path.Combine(root, "target"));
            var materializer = new StewardOwnedPrivateWorldMaterializationService(
                targetStorage,
                cache);
            var materialized = await materializer.MaterializeAsync(
                catalogEntry,
                prepared,
                owner);
            Assert.Equal(
                OwnedPrivateWorldMaterializationStatus.Materialized,
                materialized.Status);

            var targetWorld = Assert.IsType<World>(
                await targetStorage.LoadWorldAsync(worldId));
            Assert.Equal(worldId, targetWorld.Id);
            Assert.Equal(worldName, targetWorld.Name);
            Assert.Equal(adapterId, targetWorld.GameAdapterId);
            Assert.Equal(stateId, targetWorld.CurrentStateRevisionId);
            Assert.Equal(environmentId, targetWorld.CurrentEnvironmentRevisionId);
            var targetState = Assert.IsType<StateRevision>(
                await targetStorage.LoadStateRevisionAsync(worldId, stateId));
            Assert.Equal(state.Id, targetState.Id);
            Assert.Equal(state.CreatedAt, targetState.CreatedAt);
            Assert.Equal(state.CreatedBy, targetState.CreatedBy);
            var targetEnvironment = Assert.IsType<EnvironmentRevision>(
                await targetStorage.LoadEnvironmentRevisionAsync(worldId, environmentId));
            Assert.Equal(environment.Id, targetEnvironment.Id);
            Assert.Equal(environment.CreatedAt, targetEnvironment.CreatedAt);
            Assert.Equal(environment.CreatedBy, targetEnvironment.CreatedBy);
            await using (var targetPayload = await targetStorage.OpenRevisionAsync(worldId, stateId))
            {
                Assert.Equal(payload, await ReadAllBytesAsync(targetPayload));
            }

            var targetPublication = await locationService
                .PublishCurrentLocationWithPresentationAsync(
                    targetCaller,
                    worldId,
                    stateId,
                    environmentId,
                    worldName,
                    adapterId);
            Assert.Equal(
                OwnedWorldLocationWriteResult.Created,
                targetPublication.Result);
            var claims = await locations.ListWorldLocationsAsync(
                worldId,
                owner.Provider,
                owner.ExternalId);
            Assert.Equal(2, claims.Count);
            Assert.Contains(claims, claim =>
                string.Equals(
                    claim.InstallationId,
                    sourceInstallation,
                    StringComparison.Ordinal) &&
                claim.StateRevisionId == stateId &&
                claim.EnvironmentRevisionId == environmentId);
            Assert.Contains(claims, claim =>
                string.Equals(
                    claim.InstallationId,
                    targetInstallation,
                    StringComparison.Ordinal) &&
                claim.StateRevisionId == stateId &&
                claim.EnvironmentRevisionId == environmentId);

            var alreadyHere = await materializationDownload.AuthorizeAsync(
                targetCaller,
                worldId);
            Assert.Equal(
                AuthorizePrivateSnapshotDownloadStatus.AlreadyHere,
                alreadyHere.Status);
            var retry = await materializer.MaterializeAsync(
                catalogEntry,
                prepared,
                owner);
            Assert.Equal(
                OwnedPrivateWorldMaterializationStatus.AlreadyMaterialized,
                retry.Status);

            var sourceAfter = await sourceStorage.LoadWorldAsync(worldId);
            Assert.Equal(sourceWorld, sourceAfter);
            await using var sourcePayload = await sourceStorage.OpenRevisionAsync(
                worldId,
                stateId);
            Assert.Equal(payload, await ReadAllBytesAsync(sourcePayload));
        }
        finally
        {
            try
            {
                await providerClient.DeleteBucketAsync(bucket);
            }
            catch (AmazonS3Exception)
            {
                // The disposable MinIO container is destroyed after this workflow.
            }

            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch (IOException)
            {
                // Disposable test cleanup only.
            }
            catch (UnauthorizedAccessException)
            {
                // Disposable test cleanup only.
            }
        }
    }

    private static AmazonS3Client CreateS3Client(
        Uri endpoint,
        string region,
        string accessKey,
        string secretKey)
        => new(
            new BasicAWSCredentials(accessKey, secretKey),
            new AmazonS3Config
            {
                ServiceURL = endpoint.AbsoluteUri.TrimEnd('/'),
                AuthenticationRegion = region,
                ForcePathStyle = true
            });

    private static async Task UploadPartAsync(
        HttpClient client,
        DirectObjectTransferAuthorization authorization,
        byte[] payload)
    {
        Assert.Equal("PUT", authorization.Method, ignoreCase: true);
        Assert.Equal(payload.LongLength, authorization.ExpectedByteSize);
        using var request = new HttpRequestMessage(HttpMethod.Put, authorization.Uri)
        {
            Content = new ByteArrayContent(payload)
        };
        foreach (var header in authorization.RequiredHeaders)
        {
            if (string.Equals(header.Key, "Content-Length", StringComparison.OrdinalIgnoreCase))
            {
                request.Content.Headers.ContentLength = long.Parse(
                    header.Value,
                    System.Globalization.CultureInfo.InvariantCulture);
                continue;
            }

            if (!request.Headers.TryAddWithoutValidation(header.Key, header.Value) &&
                !request.Content.Headers.TryAddWithoutValidation(header.Key, header.Value))
            {
                throw new InvalidOperationException(
                    $"Could not apply required upload header '{header.Key}'.");
            }
        }

        using var response = await client.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private static byte[] CreatePayload(int length)
    {
        var bytes = new byte[length];
        for (var index = 0; index < bytes.Length; index++)
        {
            bytes[index] = (byte)((index * 31 + 17) % 251);
        }

        return bytes;
    }

    private static async Task<byte[]> ReadAllBytesAsync(Stream stream)
    {
        using var output = new MemoryStream();
        await stream.CopyToAsync(output);
        return output.ToArray();
    }

    private static string RequireEnvironment(string name)
        => Environment.GetEnvironmentVariable(name) is { Length: > 0 } value
            ? value
            : throw new InvalidOperationException(
                $"Required Bring Here acceptance environment variable '{name}' is missing.");
}
