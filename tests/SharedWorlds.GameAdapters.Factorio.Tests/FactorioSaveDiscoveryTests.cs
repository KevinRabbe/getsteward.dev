using System.Security.Cryptography;
using SharedWorlds.Core.Abstractions;
using SharedWorlds.Core.Environment;
using SharedWorlds.Core.Errors;
using SharedWorlds.GameAdapters.Factorio;

namespace SharedWorlds.GameAdapters.Factorio.Tests;

public sealed class FactorioSaveDiscoveryTests : IDisposable
{
    private const string ModSettingsHashKey = "factorio.mod-settings.sha256";

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
            var workspaceMods = Path.Combine(prepared.WorkingDirectory, "mods");

            Assert.True(File.Exists(workspaceConfig));
            var configText = await File.ReadAllTextAsync(workspaceConfig);
            Assert.Contains($"write-data={Path.GetFullPath(workspaceUserData)}", configText, StringComparison.Ordinal);
            Assert.Contains("locale=en", configText, StringComparison.Ordinal);
            Assert.True(File.Exists(Path.Combine(workspaceMods, "mod-list.json")));

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

    [Fact]
    public async Task PreparedWorkspace_CopiesOnlyExactRequiredUserModsAndVerifiedSettings()
    {
        var userData = Path.Combine(_root, "mod-isolation-source");
        var sourceMods = Path.Combine(userData, "mods");
        Directory.CreateDirectory(sourceMods);

        await CreateModDirectoryAsync(sourceMods, "required-mod_1.2.3", "required-mod", "1.2.3");
        await CreateModDirectoryAsync(sourceMods, "required-mod_1.2.4", "required-mod", "1.2.4");
        await CreateModDirectoryAsync(sourceMods, "unrelated-mod_9.0.0", "unrelated-mod", "9.0.0");

        var modSettings = new byte[] { 10, 20, 30, 40, 50 };
        var sourceModSettings = Path.Combine(sourceMods, "mod-settings.dat");
        await File.WriteAllBytesAsync(sourceModSettings, modSettings);
        var settingsHash = Convert.ToHexString(SHA256.HashData(modSettings));

        var adapter = new FactorioAdapter();
        var manifest = new EnvironmentManifest(
            SchemaVersion: 1,
            AdapterId: adapter.Id,
            GameVersion: "2.0.0",
            Components:
            [
                new EnvironmentComponent("mod", "base", "2.0.0", "builtin"),
                new EnvironmentComponent("mod", "required-mod", "1.2.3", "user")
            ],
            Configuration: new Dictionary<string, string>
            {
                [ModSettingsHashKey] = settingsHash
            });

        var prepared = await adapter.PrepareEnvironmentAsync(CreateInstallation(userData), manifest);
        try
        {
            var workspaceMods = Path.Combine(prepared.WorkingDirectory, "mods");
            Assert.True(Directory.Exists(Path.Combine(workspaceMods, "required-mod_1.2.3")));
            Assert.False(Directory.Exists(Path.Combine(workspaceMods, "required-mod_1.2.4")));
            Assert.False(Directory.Exists(Path.Combine(workspaceMods, "unrelated-mod_9.0.0")));
            Assert.Equal(
                modSettings,
                await File.ReadAllBytesAsync(Path.Combine(workspaceMods, "mod-settings.dat")));

            var modList = await File.ReadAllTextAsync(Path.Combine(workspaceMods, "mod-list.json"));
            Assert.Contains("\"name\": \"base\"", modList, StringComparison.Ordinal);
            Assert.Contains("\"name\": \"required-mod\"", modList, StringComparison.Ordinal);
            Assert.DoesNotContain("unrelated-mod", modList, StringComparison.Ordinal);
        }
        finally
        {
            await adapter.FinalizePreparedWorldAsync(prepared, PreparedWorldDisposition.Discard);
        }
    }

    [Fact]
    public async Task PreparedWorkspace_RejectsMissingExactRequiredUserMod()
    {
        var userData = Path.Combine(_root, "missing-exact-mod-source");
        var sourceMods = Path.Combine(userData, "mods");
        Directory.CreateDirectory(sourceMods);
        await CreateModDirectoryAsync(sourceMods, "required-mod_1.2.4", "required-mod", "1.2.4");

        var adapter = new FactorioAdapter();
        var manifest = new EnvironmentManifest(
            SchemaVersion: 1,
            AdapterId: adapter.Id,
            GameVersion: "2.0.0",
            Components:
            [
                new EnvironmentComponent("mod", "required-mod", "1.2.3", "user")
            ],
            Configuration: new Dictionary<string, string>());

        var exception = await Assert.ThrowsAsync<EnvironmentReproductionException>(
            () => adapter.PrepareEnvironmentAsync(CreateInstallation(userData), manifest));

        Assert.Contains("required-mod", exception.Message, StringComparison.Ordinal);
        Assert.Contains("1.2.3", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PreparedWorkspace_RejectsChangedModStartupSettings()
    {
        var userData = Path.Combine(_root, "changed-settings-source");
        var sourceMods = Path.Combine(userData, "mods");
        Directory.CreateDirectory(sourceMods);
        await File.WriteAllBytesAsync(Path.Combine(sourceMods, "mod-settings.dat"), [1, 2, 3]);

        var adapter = new FactorioAdapter();
        var manifest = new EnvironmentManifest(
            SchemaVersion: 1,
            AdapterId: adapter.Id,
            GameVersion: "2.0.0",
            Components: [],
            Configuration: new Dictionary<string, string>
            {
                [ModSettingsHashKey] = Convert.ToHexString(SHA256.HashData([9, 9, 9]))
            });

        var exception = await Assert.ThrowsAsync<EnvironmentReproductionException>(
            () => adapter.PrepareEnvironmentAsync(CreateInstallation(userData), manifest));

        Assert.Contains("startup settings", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task CreateModDirectoryAsync(
        string modsDirectory,
        string directoryName,
        string modName,
        string version)
    {
        var directory = Path.Combine(modsDirectory, directoryName);
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(
            Path.Combine(directory, "info.json"),
            $$"""
            {
              "name": "{{modName}}",
              "version": "{{version}}"
            }
            """);
        await File.WriteAllTextAsync(Path.Combine(directory, "data.lua"), "-- test mod");
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
