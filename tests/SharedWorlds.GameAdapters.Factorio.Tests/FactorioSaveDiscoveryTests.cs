using SharedWorlds.Core.Abstractions;
using SharedWorlds.Core.Environment;
using SharedWorlds.GameAdapters.Factorio;

namespace SharedWorlds.GameAdapters.Factorio.Tests;

public sealed class FactorioSaveDiscoveryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"sharedworlds-factorio-tests-{Guid.NewGuid():N}");

    [Fact]
    public async Task DiscoverWorlds_ReturnsRegularSavesAndExcludesAutosaves()
    {
        var saves = Path.Combine(_root, "saves");
        Directory.CreateDirectory(saves);
        await File.WriteAllBytesAsync(Path.Combine(saves, "our-world.zip"), [1, 2, 3]);
        await File.WriteAllBytesAsync(Path.Combine(saves, "_autosave1.zip"), [4, 5, 6]);

        var installation = CreateInstallation(_root);

        var adapter = new FactorioAdapter();
        var worlds = await adapter.DiscoverWorldsAsync(installation);

        var world = Assert.Single(worlds);
        Assert.Equal("our-world", world.DisplayName);
        Assert.True(
            world.SourcePath.EndsWith("our-world.zip", StringComparison.OrdinalIgnoreCase),
            $"Unexpected save path: {world.SourcePath}");
    }

    [Fact]
    public async Task PreparedWorkspace_RedirectsWriteDataAndCapturesOnlyIsolatedSave()
    {
        var userData = Path.Combine(_root, "user-data-source");
        var sourceSaves = Path.Combine(userData, "saves");
        var sourceConfigDirectory = Path.Combine(userData, "config");
        Directory.CreateDirectory(sourceSaves);
        Directory.CreateDirectory(sourceConfigDirectory);

        var originalSourceSave = Path.Combine(sourceSaves, "newme.zip");
        await File.WriteAllBytesAsync(originalSourceSave, [1, 2, 3, 4]);
        await File.WriteAllLinesAsync(
            Path.Combine(sourceConfigDirectory, "config.ini"),
            [
                "[path]",
                "read-data=__PATH__system-read-data__",
                $"write-data={userData}",
                "[general]",
                "locale=en"
            ]);

        var installation = CreateInstallation(userData);
        var adapter = new FactorioAdapter();
        var manifest = new EnvironmentManifest(
            SchemaVersion: 1,
            AdapterId: adapter.Id,
            GameVersion: "2.0.0",
            Components: [],
            Configuration: new Dictionary<string, string>());

        var prepared = await adapter.PrepareEnvironmentAsync(installation, manifest);
        try
        {
            var workspaceConfig = Path.Combine(prepared.WorkingDirectory, "config", "config.ini");
            var workspaceUserData = Path.Combine(prepared.WorkingDirectory, "user-data");
            var workspaceSave = Path.Combine(workspaceUserData, "saves", "world.zip");

            Assert.True(File.Exists(workspaceConfig));
            var configText = await File.ReadAllTextAsync(workspaceConfig);
            Assert.Contains($"write-data={Path.GetFullPath(workspaceUserData)}", configText, StringComparison.Ordinal);
            Assert.Contains("locale=en", configText, StringComparison.Ordinal);

            var statePackage = Path.Combine(_root, "canonical-state.zip");
            await File.WriteAllBytesAsync(statePackage, [5, 6, 7, 8]);
            await adapter.RestoreStateAsync(
                prepared,
                new StatePackage("canonical-state", statePackage));

            Assert.Equal([5, 6, 7, 8], await File.ReadAllBytesAsync(workspaceSave));

            var isolatedSavedGame = Path.Combine(workspaceUserData, "saves", "newme.zip");
            await File.WriteAllBytesAsync(isolatedSavedGame, [9, 10, 11, 12]);
            File.SetLastWriteTimeUtc(isolatedSavedGame, DateTime.UtcNow.AddMinutes(1));

            var captured = await adapter.CaptureStateAsync(prepared);
            try
            {
                Assert.Equal([9, 10, 11, 12], await File.ReadAllBytesAsync(captured.Package.Path));
                Assert.Equal([1, 2, 3, 4], await File.ReadAllBytesAsync(originalSourceSave));
            }
            finally
            {
                if (captured.DeletePackageAfterStore && File.Exists(captured.Package.Path))
                {
                    File.Delete(captured.Package.Path);
                }
            }
        }
        finally
        {
            await adapter.FinalizePreparedWorldAsync(
                prepared,
                PreparedWorldDisposition.Discard);
        }
    }

    private static GameInstallation CreateInstallation(string userDataPath)
        => new(
            Id: "test-factorio",
            RootPath: userDataPath,
            Source: "test",
            Metadata: new Dictionary<string, string>
            {
                ["userDataPath"] = userDataPath,
                ["executablePath"] = Path.Combine(userDataPath, "factorio.exe")
            });

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
            // Test cleanup must not hide the actual assertion result.
        }
        catch (UnauthorizedAccessException)
        {
            // Test cleanup must not hide the actual assertion result.
        }
    }
}
