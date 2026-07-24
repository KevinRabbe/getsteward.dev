using SharedWorlds.Core.Abstractions;
using SharedWorlds.Core.Environment;

namespace SharedWorlds.GameAdapters.Factorio.Tests;

public sealed class FactorioWorkspaceOwnershipTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"sharedworlds-factorio-ownership-{Guid.NewGuid():N}");

    [Fact]
    public void RecognizedStewardWorkspaceShapeIsAccepted()
    {
        var workingDirectory = Path.Combine(
            GetExpectedWorkRoot(),
            Guid.NewGuid().ToString("N"));

        FactorioWorkspaceOwnership.RequireOwned(workingDirectory);
    }

    [Fact]
    public async Task AdapterRefusesArbitraryRecoveryWorkspaceBeforeCaptureRestoreOrPreferenceWriteOrDelete()
    {
        var workingDirectory = Path.Combine(_root, Guid.NewGuid().ToString("N"));
        var fakeConfigRoot = Path.Combine(workingDirectory, "config");
        Directory.CreateDirectory(fakeConfigRoot);
        await File.WriteAllLinesAsync(
            Path.Combine(fakeConfigRoot, "config.ini"),
            ["[graphics]", "quality=malicious"]);

        var playerDataRoot = Path.Combine(_root, "player-data");
        var playerConfigRoot = Path.Combine(playerDataRoot, "config");
        Directory.CreateDirectory(playerConfigRoot);
        var playerConfigPath = Path.Combine(playerConfigRoot, "config.ini");
        const string originalPlayerConfig = "[graphics]\nquality=original\n[path]\nread-data=real";
        await File.WriteAllTextAsync(playerConfigPath, originalPlayerConfig);

        var adapter = new FactorioAdapter();
        var prepared = Prepared(workingDirectory, playerDataRoot);

        var capture = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            adapter.CaptureStateAsync(prepared));
        Assert.Contains("unrecognized Factorio Steward workspace", capture.Message, StringComparison.OrdinalIgnoreCase);

        var restore = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            adapter.RestoreStateAsync(
                prepared,
                new StatePackage("missing", Path.Combine(_root, "missing.zip"))));
        Assert.Contains("unrecognized Factorio Steward workspace", restore.Message, StringComparison.OrdinalIgnoreCase);

        var finalize = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            adapter.FinalizePreparedWorldAsync(
                prepared,
                PreparedWorldDisposition.Discard));
        Assert.Contains("unrecognized Factorio Steward workspace", finalize.Message, StringComparison.OrdinalIgnoreCase);

        Assert.Equal(originalPlayerConfig, await File.ReadAllTextAsync(playerConfigPath));
        Assert.True(Directory.Exists(workingDirectory));
    }

    private static PreparedWorld Prepared(string workingDirectory, string playerDataRoot)
        => new(
            new GameInstallation(
                "factorio:test",
                Path.GetTempPath(),
                "test",
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [FactorioInstallationDiscovery.UserDataPathKey] = playerDataRoot
                }),
            workingDirectory,
            new EnvironmentManifest(
                1,
                "factorio",
                "test-version",
                [],
                new Dictionary<string, string>()));

    private static string GetExpectedWorkRoot()
    {
        var basePath = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(basePath))
        {
            basePath = Path.GetTempPath();
        }

        return Path.Combine(basePath, "SharedWorlds", "factorio");
    }

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
