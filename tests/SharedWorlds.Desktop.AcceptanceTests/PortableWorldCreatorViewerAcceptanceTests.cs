using System.IO.Compression;
using SharedWorlds.Core.Abstractions;
using SharedWorlds.Core.Domain;
using SharedWorlds.Core.Environment;
using SharedWorlds.Core.Portability;
using SharedWorlds.GameAdapters.Factorio;
using SharedWorlds.Infrastructure.Portability;
using SharedWorlds.Infrastructure.Storage;
using Xunit;

namespace SharedWorlds.Desktop.AcceptanceTests;

public sealed class PortableWorldCreatorViewerAcceptanceTests
{
    [Fact]
    public async Task FactorioCreatorWorldExportsIntoIndependentViewerWorldAcrossSeparateStores()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "safe-world-creator-viewer-acceptance",
            Guid.NewGuid().ToString("N"));
        var creatorRoot = Path.Combine(root, "pc-a-creator");
        var viewerRoot = Path.Combine(root, "pc-b-viewer");
        Directory.CreateDirectory(creatorRoot);
        Directory.CreateDirectory(viewerRoot);

        var creator = new UserIdentity("local", "creator", "Creator");
        var viewer = new UserIdentity("local", "viewer", "Viewer");
        var adapter = new FactorioAdapter();
        PreparedWorld? preparedViewer = null;
        CapturedState? viewerRecapture = null;
        string? viewerPackagePath = null;
        try
        {
            var originalState = await CreateNativeSaveZipBytesAsync();
            var environment = CreateEnvironment();
            var createdAt = new DateTimeOffset(2026, 7, 27, 18, 0, 0, TimeSpan.Zero);

            var creatorStorage = new LocalWorldStorage(creatorRoot);
            var creatorWorldId = WorldId.New();
            var creatorEnvironmentId = RevisionId.New();
            var creatorStateId = RevisionId.New();
            var creatorEnvironment = new EnvironmentRevision(
                creatorEnvironmentId,
                creatorWorldId,
                ParentRevisionId: null,
                CreatedAt: createdAt,
                CreatedBy: creator,
                Manifest: environment);
            var creatorState = new StateRevision(
                creatorStateId,
                creatorWorldId,
                ParentRevisionId: null,
                CreatedAt: createdAt,
                CreatedBy: creator,
                AdapterId: adapter.Id,
                StatePackageId: creatorStateId.ToString());
            var creatorWorld = new World(
                creatorWorldId,
                "500h Megabase",
                adapter.Id,
                Members: [creator],
                CurrentEnvironmentRevisionId: creatorEnvironmentId,
                CurrentStateRevisionId: creatorStateId);

            await creatorStorage.StoreEnvironmentRevisionAsync(creatorEnvironment);
            await using (var stateInput = new MemoryStream(originalState, writable: false))
            {
                await creatorStorage.StoreRevisionAsync(creatorState, stateInput);
            }

            await creatorStorage.SaveWorldAsync(creatorWorld);

            await using var artifact = new MemoryStream();
            var exportService = new PortableWorldExportService(creatorStorage);
            var exportedManifest = await exportService.ExportCurrentAsync(
                creatorWorld,
                adapter,
                artifact,
                new PortableWorldPresentation(
                    Creator: creator.DisplayName,
                    Description: "A finished megabase.",
                    SourceUrl: "https://example.test/creator/world"));

            Assert.Equal(creatorStateId.ToString(), exportedManifest.SnapshotId);
            Assert.Equal(originalState.Length, exportedManifest.Payload.Length);
            Assert.True(artifact.Length > originalState.Length);

            // PC B starts with an unrelated local store and unrelated local user identity.
            var viewerStorage = new LocalWorldStorage(viewerRoot);
            await using var staging = new MemoryStream();
            artifact.Position = 0;
            var importService = new PortableWorldImportService(viewerStorage);
            var imported = await importService.ImportAsync(
                artifact,
                adapter,
                staging,
                viewer);

            Assert.NotEqual(creatorWorld.Id, imported.World.Id);
            Assert.NotEqual(creatorEnvironmentId, imported.World.CurrentEnvironmentRevisionId);
            Assert.NotEqual(creatorStateId, imported.World.CurrentStateRevisionId);
            Assert.Equal(viewer, Assert.Single(imported.World.Members));
            Assert.Equal(WorldSharingMode.LocalOnly, imported.World.SharingMode);
            Assert.Equal(WorldVisibility.Private, imported.World.Visibility);
            Assert.Equal(creatorStateId.ToString(), imported.World.StartedFrom?.SnapshotId);
            Assert.Equal(creator.DisplayName, imported.World.StartedFrom?.Creator);
            Assert.Equal("500h Megabase", imported.World.StartedFrom?.WorldName);
            Assert.Equal(0, staging.Length);

            var viewerWorlds = await viewerStorage.ListWorldsAsync();
            var persistedViewerWorld = Assert.Single(viewerWorlds);
            Assert.Equal(imported.World.Id, persistedViewerWorld.Id);
            Assert.Equal(viewer, Assert.Single(persistedViewerWorld.Members));

            await using (var viewerState = await viewerStorage.OpenRevisionAsync(
                             imported.World.Id,
                             imported.World.CurrentStateRevisionId!.Value))
            await using (var copied = new MemoryStream())
            {
                await viewerState.CopyToAsync(copied);
                Assert.Equal(originalState, copied.ToArray());

                viewerPackagePath = Path.Combine(root, "viewer-canonical-state.zip");
                await File.WriteAllBytesAsync(viewerPackagePath, copied.ToArray());
            }

            // Complete the same managed-workspace boundary that the normal Continue lifecycle uses
            // after Core allocates recovery identity and materializes the viewer's current immutable state.
            var viewerWorkspace = CreateManagedViewerWorkspace(root);
            preparedViewer = new PreparedWorld(
                Installation: new GameInstallation(
                    Id: "viewer-factorio-installation",
                    RootPath: viewerRoot,
                    Source: "acceptance"),
                WorkingDirectory: viewerWorkspace,
                Environment: environment,
                DisplayName: imported.World.Name,
                RecoveryLocation: PreparedWorldRecoveryLocation.Managed());

            await adapter.RestoreStateAsync(
                preparedViewer,
                new StatePackage(
                    Id: imported.World.CurrentStateRevisionId.Value.ToString(),
                    Path: viewerPackagePath));

            var restoredViewerSave = Path.Combine(
                viewerWorkspace,
                "user-data",
                "saves",
                "500h Megabase.zip");
            Assert.Equal(originalState, await File.ReadAllBytesAsync(restoredViewerSave));

            viewerRecapture = await adapter.CaptureStateAsync(preparedViewer);
            Assert.Equal(
                originalState,
                await File.ReadAllBytesAsync(viewerRecapture.Package.Path));
        }
        finally
        {
            if (preparedViewer is not null)
            {
                await adapter.FinalizePreparedWorldAsync(
                    preparedViewer,
                    PreparedWorldDisposition.Discard);
            }

            if (viewerRecapture is not null && File.Exists(viewerRecapture.Package.Path))
            {
                File.Delete(viewerRecapture.Package.Path);
            }

            if (viewerPackagePath is not null && File.Exists(viewerPackagePath))
            {
                File.Delete(viewerPackagePath);
            }

            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static EnvironmentManifest CreateEnvironment()
        => new(
            SchemaVersion: 1,
            AdapterId: "factorio",
            GameVersion: "2.0.72",
            Components: [],
            Configuration: new Dictionary<string, string>());

    private static async Task<byte[]> CreateNativeSaveZipBytesAsync()
    {
        await using var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = archive.CreateEntry(
                "500h Megabase/level.dat",
                CompressionLevel.NoCompression);
            await using var entryStream = entry.Open();
            await entryStream.WriteAsync("factorio-portable-acceptance-state"u8.ToArray());
        }

        return output.ToArray();
    }

    private static string CreateManagedViewerWorkspace(string root)
    {
        var workspace = Path.Combine(
            root,
            "managed-workspaces",
            WorkspaceId.New().ToString());
        Directory.CreateDirectory(workspace);
        return workspace;
    }
}
