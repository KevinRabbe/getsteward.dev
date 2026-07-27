using SharedWorlds.Core.Abstractions;
using SharedWorlds.Core.Domain;
using SharedWorlds.Core.Environment;
using SharedWorlds.Core.Portability;
using SharedWorlds.Infrastructure.Portability;
using SharedWorlds.Infrastructure.Storage;
using Xunit;

namespace SharedWorlds.Infrastructure.Tests;

public sealed class PortableWorldImportServiceTests
{
    private static readonly UserIdentity Viewer = new(
        Provider: "local",
        ExternalId: "viewer",
        DisplayName: "Viewer");

    [Fact]
    public async Task ImportCreatesIndependentLocalWorldOwnedByViewerWithExactPortableStateAndEnvironment()
    {
        var root = CreateRoot();
        try
        {
            var storage = new LocalWorldStorage(root);
            var service = new PortableWorldImportService(storage);
            var sourceBytes = "creator-portable-state"u8.ToArray();
            var sourceCreatedAt = new DateTimeOffset(2026, 7, 27, 18, 30, 0, TimeSpan.Zero);
            const string sourceSnapshotId = "source-snapshot-123";

            await using var artifact = await CreateArtifactAsync(
                sourceBytes,
                sourceSnapshotId,
                sourceCreatedAt,
                creator: "Creator");
            await using var staging = new MemoryStream();

            var result = await service.ImportAsync(
                artifact,
                new QualifiedAdapter("factorio"),
                staging,
                Viewer);

            Assert.Equal("500h Megabase", result.World.Name);
            Assert.Equal("factorio", result.World.GameAdapterId);
            Assert.Equal(Viewer, Assert.Single(result.World.Members));
            Assert.Equal(WorldSharingMode.LocalOnly, result.World.SharingMode);
            Assert.Equal(WorldGameVersionPolicy.KeepExact, result.World.GameVersionPolicy);
            Assert.Equal(WorldVisibility.Private, result.World.Visibility);
            Assert.Equal(WorldJoinPolicy.InviteOrCodeOnly, result.World.JoinPolicy);
            Assert.Equal(StartYourOwnPolicy.Disabled, result.World.StartYourOwnPolicy);
            Assert.NotNull(result.World.CurrentEnvironmentRevisionId);
            Assert.NotNull(result.World.CurrentStateRevisionId);
            Assert.NotEqual(sourceSnapshotId, result.World.CurrentStateRevisionId?.ToString());

            var provenance = Assert.IsType<WorldProvenance>(result.World.StartedFrom);
            Assert.Equal(sourceSnapshotId, provenance.SnapshotId);
            Assert.Equal("500h Megabase", provenance.WorldName);
            Assert.Equal(sourceCreatedAt, provenance.SnapshotCreatedAt);
            Assert.Equal("Creator", provenance.Creator);
            Assert.Equal("A finished megabase.", provenance.Description);
            Assert.Equal("https://example.test/world", provenance.SourceUrl);
            Assert.NotEqual(Viewer.DisplayName, provenance.Creator);

            var listed = Assert.Single(await storage.ListWorldsAsync());
            Assert.Equal(result.World.Id, listed.Id);
            Assert.Equal(Viewer, Assert.Single(listed.Members));
            Assert.Equal(provenance, listed.StartedFrom);

            var environment = await storage.LoadEnvironmentRevisionAsync(
                result.World.Id,
                result.World.CurrentEnvironmentRevisionId!.Value);
            Assert.NotNull(environment);
            Assert.Equal(Viewer, environment!.CreatedBy);
            Assert.Equal("factorio", environment.Manifest.AdapterId);
            Assert.Equal("2.0.72", environment.Manifest.GameVersion);

            var stateRevision = await storage.LoadStateRevisionAsync(
                result.World.Id,
                result.World.CurrentStateRevisionId!.Value);
            Assert.NotNull(stateRevision);
            Assert.Equal(Viewer, stateRevision!.CreatedBy);

            await using var storedState = await storage.OpenRevisionAsync(
                result.World.Id,
                result.World.CurrentStateRevisionId!.Value);
            await using var copied = new MemoryStream();
            await storedState.CopyToAsync(copied);
            Assert.Equal(sourceBytes, copied.ToArray());

            Assert.Equal(0, staging.Length);
            Assert.Equal(0, staging.Position);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task ImportingSameArtifactTwiceCreatesTwoUnrelatedWorldsOwnedByViewer()
    {
        var root = CreateRoot();
        try
        {
            var storage = new LocalWorldStorage(root);
            var service = new PortableWorldImportService(storage);
            await using var artifact = await CreateArtifactAsync(
                "same-source"u8.ToArray(),
                "shared-snapshot",
                DateTimeOffset.UtcNow,
                creator: "Creator");

            await using var firstStaging = new MemoryStream();
            var first = await service.ImportAsync(
                artifact,
                new QualifiedAdapter("factorio"),
                firstStaging,
                Viewer);

            artifact.Position = 0;
            await using var secondStaging = new MemoryStream();
            var second = await service.ImportAsync(
                artifact,
                new QualifiedAdapter("factorio"),
                secondStaging,
                Viewer);

            Assert.NotEqual(first.World.Id, second.World.Id);
            Assert.NotEqual(first.World.CurrentEnvironmentRevisionId, second.World.CurrentEnvironmentRevisionId);
            Assert.NotEqual(first.World.CurrentStateRevisionId, second.World.CurrentStateRevisionId);
            Assert.Equal(Viewer, Assert.Single(first.World.Members));
            Assert.Equal(Viewer, Assert.Single(second.World.Members));
            Assert.Equal("shared-snapshot", first.World.StartedFrom?.SnapshotId);
            Assert.Equal("shared-snapshot", second.World.StartedFrom?.SnapshotId);
            Assert.Equal(2, (await storage.ListWorldsAsync()).Count);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task ImportRequiresExplicitLocalOwnerBeforeReadingArtifact()
    {
        var root = CreateRoot();
        try
        {
            var service = new PortableWorldImportService(new LocalWorldStorage(root));
            await using var artifact = new MemoryStream("not-read"u8.ToArray());
            await using var staging = new MemoryStream();

            await Assert.ThrowsAsync<ArgumentNullException>(() =>
                service.ImportAsync(
                    artifact,
                    new QualifiedAdapter("factorio"),
                    staging,
                    owner: null!));

            Assert.Equal(0, artifact.Position);
            Assert.Equal(0, staging.Length);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task ImportRejectsWrongAdapterBeforePublishingWorld()
    {
        var root = CreateRoot();
        try
        {
            var storage = new LocalWorldStorage(root);
            var service = new PortableWorldImportService(storage);
            await using var artifact = await CreateArtifactAsync(
                "state"u8.ToArray(),
                "snapshot",
                DateTimeOffset.UtcNow,
                creator: null);
            await using var staging = new MemoryStream();

            var exception = await Assert.ThrowsAsync<NotSupportedException>(() =>
                service.ImportAsync(
                    artifact,
                    new QualifiedAdapter("different-game"),
                    staging,
                    Viewer));

            Assert.Contains("factorio", exception.Message, StringComparison.Ordinal);
            Assert.Empty(await storage.ListWorldsAsync());
            Assert.Equal(0, staging.Length);
            Assert.Equal(0, staging.Position);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task ImportRejectsAdapterWithoutPublicPortabilityAuthority()
    {
        var root = CreateRoot();
        try
        {
            var storage = new LocalWorldStorage(root);
            var service = new PortableWorldImportService(storage);
            await using var artifact = await CreateArtifactAsync(
                "state"u8.ToArray(),
                "snapshot",
                DateTimeOffset.UtcNow,
                creator: null);
            await using var staging = new MemoryStream();

            var exception = await Assert.ThrowsAsync<NotSupportedException>(() =>
                service.ImportAsync(
                    artifact,
                    new PrivateOnlyAdapter("factorio"),
                    staging,
                    Viewer));

            Assert.Contains("public portable Worlds", exception.Message, StringComparison.Ordinal);
            Assert.Empty(await storage.ListWorldsAsync());
            Assert.Equal(0, staging.Length);
            Assert.Equal(0, staging.Position);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task ImportRejectsEnvironmentNotApprovedForPublicPortability()
    {
        var root = CreateRoot();
        try
        {
            var storage = new LocalWorldStorage(root);
            var service = new PortableWorldImportService(storage);
            await using var artifact = await CreateArtifactAsync(
                "state"u8.ToArray(),
                "snapshot",
                DateTimeOffset.UtcNow,
                creator: null);
            await using var staging = new MemoryStream();
            const string reason = "This exact environment has not earned the public portability claim.";

            var exception = await Assert.ThrowsAsync<NotSupportedException>(() =>
                service.ImportAsync(
                    artifact,
                    new QualifiedAdapter(
                        "factorio",
                        PublicWorldExportReadiness.Unsupported(reason)),
                    staging,
                    Viewer));

            Assert.Equal(reason, exception.Message);
            Assert.Empty(await storage.ListWorldsAsync());
            Assert.Equal(0, staging.Length);
            Assert.Equal(0, staging.Position);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task InvalidArtifactLeavesNoPublishedWorldAndClearsStaging()
    {
        var root = CreateRoot();
        try
        {
            var storage = new LocalWorldStorage(root);
            var service = new PortableWorldImportService(storage);
            await using var artifact = new MemoryStream("not-a-safe-world"u8.ToArray());
            await using var staging = new MemoryStream();

            await Assert.ThrowsAnyAsync<Exception>(() =>
                service.ImportAsync(
                    artifact,
                    new PrivateOnlyAdapter("factorio"),
                    staging,
                    Viewer));

            Assert.Empty(await storage.ListWorldsAsync());
            Assert.Equal(0, staging.Length);
            Assert.Equal(0, staging.Position);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    private static async Task<MemoryStream> CreateArtifactAsync(
        byte[] state,
        string snapshotId,
        DateTimeOffset createdAt,
        string? creator)
    {
        var artifact = new MemoryStream();
        await using var stateStream = new MemoryStream(state, writable: false);
        await PortableWorldArchive.WriteAsync(
            artifact,
            new PortableWorldDescription(
                GameAdapterId: "factorio",
                WorldName: "500h Megabase",
                SnapshotId: snapshotId,
                CreatedAt: createdAt,
                Environment: CreateEnvironment(),
                Presentation: new PortableWorldPresentation(
                    Creator: creator,
                    Description: "A finished megabase.",
                    SourceUrl: "https://example.test/world")),
            stateStream);
        artifact.Position = 0;
        return artifact;
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
            "sharedworlds-portable-import-tests",
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

    private abstract class TestAdapterBase(string id) : IGameAdapter
    {
        public string Id { get; } = id;
        public string DisplayName => Id;
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

    private sealed class QualifiedAdapter(
        string id,
        PublicWorldExportReadiness? readiness = null)
        : TestAdapterBase(id), IPublicWorldExportAdapter
    {
        public Task<PublicWorldExportReadiness> CheckPublicWorldExportAsync(
            EnvironmentManifest exactEnvironment,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(readiness ?? PublicWorldExportReadiness.Supported());
        }
    }

    private sealed class PrivateOnlyAdapter(string id) : TestAdapterBase(id);
}
