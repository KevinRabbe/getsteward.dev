using SharedWorlds.Core.Abstractions;
using SharedWorlds.Core.Domain;
using SharedWorlds.Core.Environment;
using SharedWorlds.Infrastructure.Portability;
using SharedWorlds.Infrastructure.Storage;
using Xunit;

namespace SharedWorlds.Infrastructure.Tests;

public sealed class PortableWorldExportServiceTests
{
    [Fact]
    public async Task ExportCurrentUsesOnlyCurrentCommittedStateAndEnvironment()
    {
        var root = CreateRoot();
        try
        {
            var storage = new LocalWorldStorage(root);
            var worldId = WorldId.New();
            var oldStateId = RevisionId.New();
            var currentStateId = RevisionId.New();
            var environmentId = RevisionId.New();
            var createdAt = new DateTimeOffset(2026, 7, 27, 19, 30, 0, TimeSpan.Zero);

            var environment = CreateEnvironment();
            await storage.StoreEnvironmentRevisionAsync(new EnvironmentRevision(
                environmentId,
                worldId,
                ParentRevisionId: null,
                CreatedAt: createdAt,
                CreatedBy: null,
                Manifest: environment));

            await StoreStateAsync(storage, worldId, oldStateId, "old-state"u8.ToArray(), createdAt.AddMinutes(-10));
            var currentBytes = "current-state"u8.ToArray();
            await StoreStateAsync(storage, worldId, currentStateId, currentBytes, createdAt);

            var world = new World(
                worldId,
                "500h Megabase",
                "factorio",
                Members: [],
                CurrentEnvironmentRevisionId: environmentId,
                CurrentStateRevisionId: currentStateId);

            var service = new PortableWorldExportService(storage);
            await using var artifact = new MemoryStream();
            var manifest = await service.ExportCurrentAsync(
                world,
                new ExportAdapter(PublicWorldExportReadiness.Supported()),
                artifact,
                presentation: new("Creator", "A finished megabase."));

            Assert.Equal(currentStateId.ToString(), manifest.SnapshotId);
            Assert.Equal(createdAt, manifest.CreatedAt);
            Assert.Equal("500h Megabase", manifest.WorldName);
            Assert.Equal("Creator", manifest.Presentation?.Creator);
            Assert.Equal(0, artifact.Position);

            await using var extracted = new MemoryStream();
            var validated = await PortableWorldArchive.ValidateAndExtractStateAsync(artifact, extracted);
            Assert.Equal(currentStateId.ToString(), validated.SnapshotId);
            Assert.Equal(currentBytes, extracted.ToArray());
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task ExportFailsClosedWhenAdapterDoesNotAuthorizeExactEnvironment()
    {
        var root = CreateRoot();
        try
        {
            var (storage, world) = await CreateCurrentWorldAsync(root);
            var service = new PortableWorldExportService(storage);
            await using var artifact = new MemoryStream();

            var exception = await Assert.ThrowsAsync<NotSupportedException>(() =>
                service.ExportCurrentAsync(
                    world,
                    new ExportAdapter(PublicWorldExportReadiness.Unsupported(
                        "This exact environment is not public-safe.")),
                    artifact));

            Assert.Contains("not public-safe", exception.Message, StringComparison.Ordinal);
            Assert.Equal(0, artifact.Length);
            Assert.Equal(0, artifact.Position);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task ExportFailsClosedWhenAdapterHasNoPublicExportAuthority()
    {
        var root = CreateRoot();
        try
        {
            var (storage, world) = await CreateCurrentWorldAsync(root);
            var service = new PortableWorldExportService(storage);
            await using var artifact = new MemoryStream();

            await Assert.ThrowsAsync<NotSupportedException>(() =>
                service.ExportCurrentAsync(world, new PrivateOnlyAdapter(), artifact));

            Assert.Equal(0, artifact.Length);
            Assert.Equal(0, artifact.Position);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task ExportDoesNotFallBackWhenCurrentStateRevisionIsMissing()
    {
        var root = CreateRoot();
        try
        {
            var storage = new LocalWorldStorage(root);
            var worldId = WorldId.New();
            var environmentId = RevisionId.New();
            await storage.StoreEnvironmentRevisionAsync(new EnvironmentRevision(
                environmentId,
                worldId,
                ParentRevisionId: null,
                CreatedAt: DateTimeOffset.UtcNow,
                CreatedBy: null,
                Manifest: CreateEnvironment()));

            var world = new World(
                worldId,
                "Incomplete World",
                "factorio",
                Members: [],
                CurrentEnvironmentRevisionId: environmentId,
                CurrentStateRevisionId: RevisionId.New());

            var service = new PortableWorldExportService(storage);
            await using var artifact = new MemoryStream();

            await Assert.ThrowsAsync<InvalidDataException>(() =>
                service.ExportCurrentAsync(
                    world,
                    new ExportAdapter(PublicWorldExportReadiness.Supported()),
                    artifact));

            Assert.Equal(0, artifact.Length);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    private static async Task<(LocalWorldStorage Storage, World World)> CreateCurrentWorldAsync(string root)
    {
        var storage = new LocalWorldStorage(root);
        var worldId = WorldId.New();
        var environmentId = RevisionId.New();
        var stateId = RevisionId.New();
        var now = DateTimeOffset.UtcNow;

        await storage.StoreEnvironmentRevisionAsync(new EnvironmentRevision(
            environmentId,
            worldId,
            ParentRevisionId: null,
            CreatedAt: now,
            CreatedBy: null,
            Manifest: CreateEnvironment()));
        await StoreStateAsync(storage, worldId, stateId, "state"u8.ToArray(), now);

        return (
            storage,
            new World(
                worldId,
                "World",
                "factorio",
                Members: [],
                CurrentEnvironmentRevisionId: environmentId,
                CurrentStateRevisionId: stateId));
    }

    private static async Task StoreStateAsync(
        IWorldStorage storage,
        WorldId worldId,
        RevisionId revisionId,
        byte[] payload,
        DateTimeOffset createdAt)
    {
        var revision = new StateRevision(
            revisionId,
            worldId,
            ParentRevisionId: null,
            CreatedAt: createdAt,
            CreatedBy: null,
            AdapterId: "factorio",
            StatePackageId: revisionId.ToString());
        await using var stream = new MemoryStream(payload, writable: false);
        await storage.StoreRevisionAsync(revision, stream);
    }

    private static EnvironmentManifest CreateEnvironment()
        => new(
            SchemaVersion: 1,
            AdapterId: "factorio",
            GameVersion: "2.0.72",
            Components: [],
            Configuration: new Dictionary<string, string>());

    private static string CreateRoot()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            "sharedworlds-portable-export-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void DeleteRoot(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
    }

    private sealed class ExportAdapter(PublicWorldExportReadiness readiness)
        : PrivateOnlyAdapter, IPublicWorldExportAdapter
    {
        public Task<PublicWorldExportReadiness> CheckPublicWorldExportAsync(
            EnvironmentManifest exactEnvironment,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(readiness);
        }
    }

    private class PrivateOnlyAdapter : IGameAdapter
    {
        public string Id => "factorio";
        public string DisplayName => "Factorio";
        public GameAdapterCapabilities Capabilities => GameAdapterCapabilities.None;

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
}
