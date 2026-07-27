using System.IO.Compression;
using System.Text;
using SharedWorlds.Core.Abstractions;
using SharedWorlds.Core.Environment;
using SharedWorlds.GameAdapters.Factorio;
using Xunit;

namespace SharedWorlds.GameAdapters.Factorio.Tests;

public sealed class FactorioPublicWorldExportTests
{
    [Fact]
    public async Task CurrentFactorioEnvironmentIsQualifiedForPublicExport()
    {
        var adapter = new FactorioAdapter();
        var environment = CreateEnvironment(schemaVersion: 1, adapterId: "factorio");

        var readiness = await ((IPublicWorldExportAdapter)adapter)
            .CheckPublicWorldExportAsync(environment);

        Assert.True(readiness.IsSupported);
        Assert.Null(readiness.Reason);
    }

    [Fact]
    public async Task FutureEnvironmentSchemaFailsClosed()
    {
        var adapter = new FactorioAdapter();
        var environment = CreateEnvironment(schemaVersion: 2, adapterId: "factorio");

        var readiness = await ((IPublicWorldExportAdapter)adapter)
            .CheckPublicWorldExportAsync(environment);

        Assert.False(readiness.IsSupported);
        Assert.Contains("has not been qualified", readiness.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WrongAdapterEnvironmentFailsClosed()
    {
        var adapter = new FactorioAdapter();
        var environment = CreateEnvironment(schemaVersion: 1, adapterId: "palworld");

        var readiness = await ((IPublicWorldExportAdapter)adapter)
            .CheckPublicWorldExportAsync(environment);

        Assert.False(readiness.IsSupported);
        Assert.Contains("not Factorio", readiness.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DetectedWorldCaptureCopiesOnlyNativeSaveZip()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "sharedworlds-factorio-public-export-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        var savePath = Path.Combine(root, "500h-megabase.zip");
        var saveBytes = Encoding.UTF8.GetBytes("factorio-save-marker");
        await File.WriteAllBytesAsync(savePath, saveBytes);

        var fakeSecret = "do-not-publish-this-token";
        await File.WriteAllTextAsync(
            Path.Combine(root, "player-data.json"),
            $"{{\"token\":\"{fakeSecret}\"}}");
        await File.WriteAllTextAsync(
            Path.Combine(root, "config.ini"),
            "[path]\nwrite-data=private-device-path");

        CapturedState? captured = null;
        try
        {
            var adapter = new FactorioAdapter();
            captured = await adapter.CaptureDetectedWorldAsync(
                new GameInstallation(
                    Id: "test-installation",
                    RootPath: root,
                    Source: "test"),
                new DetectedWorld(
                    Id: savePath,
                    DisplayName: "500h Megabase",
                    SourcePath: savePath));

            var capturedBytes = await File.ReadAllBytesAsync(captured.Package.Path);
            Assert.Equal(saveBytes, capturedBytes);
            Assert.DoesNotContain(
                fakeSecret,
                Encoding.UTF8.GetString(capturedBytes),
                StringComparison.Ordinal);
        }
        finally
        {
            DeleteCapturedPackage(captured);
            DeleteDirectory(root);
        }
    }

    [Fact]
    public async Task PublicCapturedStateRestoresAndRecapturesByteForByteForIndependentViewer()
    {
        var sourceRoot = Path.Combine(
            Path.GetTempPath(),
            "sharedworlds-factorio-public-viewer-contract-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(sourceRoot);

        var sourceSavePath = Path.Combine(sourceRoot, "500h Megabase.zip");
        await CreateNativeSaveZipAsync(sourceSavePath);
        var originalBytes = await File.ReadAllBytesAsync(sourceSavePath);

        var adapter = new FactorioAdapter();
        CapturedState? creatorCapture = null;
        CapturedState? viewerRecapture = null;
        PreparedWorld? viewerWorld = null;
        try
        {
            creatorCapture = await adapter.CaptureDetectedWorldAsync(
                new GameInstallation(
                    Id: "creator-installation",
                    RootPath: sourceRoot,
                    Source: "test"),
                new DetectedWorld(
                    Id: sourceSavePath,
                    DisplayName: "500h Megabase",
                    SourcePath: sourceSavePath));

            Assert.Equal(
                originalBytes,
                await File.ReadAllBytesAsync(creatorCapture.Package.Path));

            var viewerWorkspace = CreateOwnedViewerWorkspace();
            viewerWorld = new PreparedWorld(
                Installation: new GameInstallation(
                    Id: "viewer-installation",
                    RootPath: sourceRoot,
                    Source: "test"),
                WorkingDirectory: viewerWorkspace,
                Environment: CreateEnvironment(schemaVersion: 1, adapterId: "factorio"),
                DisplayName: "500h Megabase");

            // This StatePackage is the exact adapter-owned byte boundary placed in state.bin by
            // .safeworld and handed back to the adapter after the generic archive layer verifies it.
            await adapter.RestoreStateAsync(
                viewerWorld,
                new StatePackage(
                    Id: "portable-viewer-state",
                    Path: creatorCapture.Package.Path));

            var restoredPath = Path.Combine(
                viewerWorkspace,
                "user-data",
                "saves",
                "500h Megabase.zip");
            Assert.Equal(originalBytes, await File.ReadAllBytesAsync(restoredPath));

            viewerRecapture = await adapter.CaptureStateAsync(viewerWorld);
            Assert.Equal(
                originalBytes,
                await File.ReadAllBytesAsync(viewerRecapture.Package.Path));
        }
        finally
        {
            if (viewerWorld is not null)
            {
                await adapter.FinalizePreparedWorldAsync(
                    viewerWorld,
                    PreparedWorldDisposition.Discard);
            }

            DeleteCapturedPackage(viewerRecapture);
            DeleteCapturedPackage(creatorCapture);
            DeleteDirectory(sourceRoot);
        }
    }

    private static async Task CreateNativeSaveZipAsync(string path)
    {
        await using var output = new FileStream(
            path,
            FileMode.CreateNew,
            FileAccess.ReadWrite,
            FileShare.None,
            bufferSize: 16 * 1024,
            useAsync: true);
        using var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);
        var entry = archive.CreateEntry("500h Megabase/level.dat", CompressionLevel.NoCompression);
        await using var entryStream = entry.Open();
        await entryStream.WriteAsync("factorio-native-save-marker"u8.ToArray());
    }

    private static string CreateOwnedViewerWorkspace()
    {
        var localDataRoot = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localDataRoot))
        {
            localDataRoot = Path.GetTempPath();
        }

        var workspace = Path.Combine(
            localDataRoot,
            "SharedWorlds",
            "factorio",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workspace);
        return workspace;
    }

    private static void DeleteCapturedPackage(CapturedState? captured)
    {
        if (captured is not null && File.Exists(captured.Package.Path))
        {
            File.Delete(captured.Package.Path);
        }
    }

    private static void DeleteDirectory(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
    }

    private static EnvironmentManifest CreateEnvironment(int schemaVersion, string adapterId)
        => new(
            SchemaVersion: schemaVersion,
            AdapterId: adapterId,
            GameVersion: "2.0.72",
            Components:
            [
                new EnvironmentComponent(
                    Kind: "mod",
                    Id: "base",
                    Version: "2.0.72",
                    Source: "builtin"),
                new EnvironmentComponent(
                    Kind: "mod",
                    Id: "example-user-mod",
                    Version: "1.2.3",
                    Source: "user")
            ],
            Configuration: new Dictionary<string, string>
            {
                ["factorio.mod-settings.sha256"] = new string('A', 64)
            });
}
