using SharedWorlds.Core.Abstractions;
using SharedWorlds.Core.Environment;

namespace SharedWorlds.GameAdapters.Factorio.Tests;

public sealed class FactorioModInputSafetyTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"sharedworlds-factorio-input-safety-{Guid.NewGuid():N}");

    [Fact]
    public async Task InspectionRejectsLinkedModsDirectoryBeforeReadingEnvironment()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var userData = Path.Combine(_root, "inspection-linked-mods-user-data");
        Directory.CreateDirectory(userData);
        var outsideMods = Path.Combine(_root, "outside-inspection-mods");
        Directory.CreateDirectory(outsideMods);
        await File.WriteAllTextAsync(Path.Combine(outsideMods, "mod-list.json"), "{\"mods\":[]}");
        await File.WriteAllBytesAsync(Path.Combine(outsideMods, "mod-settings.dat"), [1, 2, 3]);
        var linkedMods = Path.Combine(userData, "mods");
        Directory.CreateSymbolicLink(linkedMods, outsideMods);

        try
        {
            var adapter = new FactorioAdapter();
            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                adapter.InspectEnvironmentAsync(
                    Installation(userData),
                    new DetectedWorld("test", "Test", Path.Combine(_root, "test.zip"))));

            Assert.Contains("mods directory", exception.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("linked or a reparse point", exception.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(linkedMods);
        }
    }

    [Fact]
    public async Task ReproductionRejectsLinkedModsDirectoryBeforeWorkspaceWork()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var userData = Path.Combine(_root, "reproduction-linked-mods-user-data");
        Directory.CreateDirectory(userData);
        var outsideMods = Path.Combine(_root, "outside-reproduction-mods");
        Directory.CreateDirectory(outsideMods);
        await File.WriteAllBytesAsync(Path.Combine(outsideMods, "mod-settings.dat"), [4, 5, 6]);
        var linkedMods = Path.Combine(userData, "mods");
        Directory.CreateSymbolicLink(linkedMods, outsideMods);

        try
        {
            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                FactorioWorldOperations.PrepareEnvironmentAsync(
                    Installation(userData),
                    new EnvironmentManifest(
                        1,
                        "factorio",
                        "test-version",
                        [],
                        new Dictionary<string, string>()),
                    CancellationToken.None));

            Assert.Contains("mods directory", exception.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("linked or a reparse point", exception.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(linkedMods);
        }
    }

    [Fact]
    public async Task InspectionRejectsLinkedModListBeforeReadingEnvironment()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var userData = Path.Combine(_root, "inspection-user-data");
        var mods = Path.Combine(userData, "mods");
        Directory.CreateDirectory(mods);
        var outside = Path.Combine(_root, "outside-mod-list.json");
        await File.WriteAllTextAsync(outside, "{\"mods\":[]}");
        var linked = Path.Combine(mods, "mod-list.json");
        File.CreateSymbolicLink(linked, outside);

        try
        {
            var adapter = new FactorioAdapter();
            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                adapter.InspectEnvironmentAsync(
                    Installation(userData),
                    new DetectedWorld("test", "Test", Path.Combine(_root, "test.zip"))));

            Assert.Contains("mod-list", exception.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("linked or a reparse point", exception.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            File.Delete(linked);
        }
    }

    [Fact]
    public async Task InspectionRejectsOversizedModListBeforeJsonParse()
    {
        var installationRoot = Path.Combine(_root, "oversized-mod-list-install");
        var baseData = Path.Combine(installationRoot, "data", "base");
        Directory.CreateDirectory(baseData);
        await File.WriteAllTextAsync(
            Path.Combine(baseData, "info.json"),
            "{\"version\":\"2.0.0\"}");

        var userData = Path.Combine(_root, "oversized-mod-list-user-data");
        var mods = Path.Combine(userData, "mods");
        Directory.CreateDirectory(mods);
        var modListPath = Path.Combine(mods, "mod-list.json");
        using (var stream = new FileStream(
                   modListPath,
                   FileMode.CreateNew,
                   FileAccess.Write,
                   FileShare.None))
        {
            stream.SetLength(FactorioEnvironmentInspector.MaximumModListBytes + 1L);
        }

        var adapter = new FactorioAdapter();
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            adapter.InspectEnvironmentAsync(
                Installation(userData, installationRoot),
                new DetectedWorld("test", "Test", Path.Combine(_root, "test.zip"))));

        Assert.Contains("mod-list", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("safety limit", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(
            FactorioEnvironmentInspector.MaximumModListBytes + 1L,
            new FileInfo(modListPath).Length);
    }

    [Fact]
    public async Task InspectionRejectsLinkedStartupSettingsBeforeReadingEnvironment()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var userData = Path.Combine(_root, "inspection-settings-user-data");
        var mods = Path.Combine(userData, "mods");
        Directory.CreateDirectory(mods);
        var outside = Path.Combine(_root, "outside-mod-settings.dat");
        await File.WriteAllBytesAsync(outside, [1, 2, 3]);
        var linked = Path.Combine(mods, "mod-settings.dat");
        File.CreateSymbolicLink(linked, outside);

        try
        {
            var adapter = new FactorioAdapter();
            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                adapter.InspectEnvironmentAsync(
                    Installation(userData),
                    new DetectedWorld("test", "Test", Path.Combine(_root, "test.zip"))));

            Assert.Contains("startup-settings", exception.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("linked or a reparse point", exception.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            File.Delete(linked);
        }
    }

    [Fact]
    public async Task ReproductionRejectsLinkedStartupSettingsBeforeWorkspaceWork()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var userData = Path.Combine(_root, "reproduction-user-data");
        var mods = Path.Combine(userData, "mods");
        Directory.CreateDirectory(mods);
        var outside = Path.Combine(_root, "outside-reproduction-settings.dat");
        await File.WriteAllBytesAsync(outside, [4, 5, 6]);
        var linked = Path.Combine(mods, "mod-settings.dat");
        File.CreateSymbolicLink(linked, outside);

        try
        {
            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                FactorioWorldOperations.PrepareEnvironmentAsync(
                    Installation(userData),
                    new EnvironmentManifest(
                        1,
                        "factorio",
                        "test-version",
                        [],
                        new Dictionary<string, string>()),
                    CancellationToken.None));

            Assert.Contains("startup-settings", exception.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("linked or a reparse point", exception.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            File.Delete(linked);
        }
    }

    [Fact]
    public void ReproductionIgnoresSourceModListBecauseStewardGeneratesItsOwn()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var userData = Path.Combine(_root, "reproduction-mod-list-user-data");
        var mods = Path.Combine(userData, "mods");
        Directory.CreateDirectory(mods);
        var outside = Path.Combine(_root, "outside-reproduction-mod-list.json");
        File.WriteAllText(outside, "{\"mods\":[]}");
        var linked = Path.Combine(mods, "mod-list.json");
        File.CreateSymbolicLink(linked, outside);

        try
        {
            FactorioModInputSafety.RequireReproductionInputs(Installation(userData));
        }
        finally
        {
            File.Delete(linked);
        }
    }

    [Fact]
    public void OrdinaryEnvironmentInputFilesAreAccepted()
    {
        var userData = Path.Combine(_root, "ordinary-user-data");
        var mods = Path.Combine(userData, "mods");
        Directory.CreateDirectory(mods);
        File.WriteAllText(Path.Combine(mods, "mod-list.json"), "{\"mods\":[]}");
        File.WriteAllBytes(Path.Combine(mods, "mod-settings.dat"), [1, 2, 3]);

        var installation = Installation(userData);
        FactorioModInputSafety.RequireInspectionInputs(installation);
        FactorioModInputSafety.RequireReproductionInputs(installation);
    }

    private static GameInstallation Installation(
        string userData,
        string? installationRoot = null)
        => new(
            "factorio:test",
            installationRoot ?? Path.Combine(Path.GetTempPath(), "factorio-test-install"),
            "test",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [FactorioInstallationDiscovery.UserDataPathKey] = userData
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
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
