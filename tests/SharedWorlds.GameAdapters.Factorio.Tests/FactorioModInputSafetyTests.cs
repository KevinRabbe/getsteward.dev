using SharedWorlds.Core.Abstractions;
using SharedWorlds.Core.Environment;

namespace SharedWorlds.GameAdapters.Factorio.Tests;

public sealed class FactorioModInputSafetyTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"sharedworlds-factorio-input-safety-{Guid.NewGuid():N}");

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
    public async Task ReproductionRejectsLinkedStartupSettingsBeforeVersionWork()
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
            var adapter = new FactorioAdapter();
            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                adapter.PrepareEnvironmentAsync(
                    Installation(userData),
                    new EnvironmentManifest(
                        1,
                        "factorio",
                        "test-version",
                        [],
                        new Dictionary<string, string>())));

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

    private static GameInstallation Installation(string userData)
        => new(
            "factorio:test",
            Path.Combine(Path.GetTempPath(), "factorio-test-install"),
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
