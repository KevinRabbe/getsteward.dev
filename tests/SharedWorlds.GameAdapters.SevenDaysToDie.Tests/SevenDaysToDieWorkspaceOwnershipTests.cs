using SharedWorlds.Core.Abstractions;
using SharedWorlds.Core.Environment;

namespace SharedWorlds.GameAdapters.SevenDaysToDie.Tests;

public sealed class SevenDaysToDieWorkspaceOwnershipTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"sharedworlds-7dtd-ownership-{Guid.NewGuid():N}");

    [Fact]
    public void RecognizedStewardWorkspaceShapeIsAccepted()
    {
        var workingDirectory = Path.Combine(
            GetExpectedWorkRoot(),
            Guid.NewGuid().ToString("N"),
            "user-data");

        SevenDaysToDieWorkspaceOwnership.RequireOwned(workingDirectory);
    }

    [Fact]
    public async Task AdapterRefusesArbitraryRecoveryWorkspaceBeforeCaptureRestoreOrDelete()
    {
        var operationRoot = Path.Combine(_root, "unowned-operation");
        var workingDirectory = Path.Combine(operationRoot, "user-data");
        Directory.CreateDirectory(workingDirectory);
        var sentinel = Path.Combine(operationRoot, "sentinel.txt");
        await File.WriteAllTextAsync(sentinel, "keep-me");
        var prepared = Prepared(workingDirectory);
        var adapter = new SevenDaysToDieAdapter();

        var capture = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            adapter.CaptureStateAsync(prepared));
        Assert.Contains("unrecognized 7 Days to Die Steward workspace", capture.Message, StringComparison.OrdinalIgnoreCase);

        var restore = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            adapter.RestoreStateAsync(
                prepared,
                new StatePackage("missing", Path.Combine(_root, "missing.zip"))));
        Assert.Contains("unrecognized 7 Days to Die Steward workspace", restore.Message, StringComparison.OrdinalIgnoreCase);

        var finalize = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            adapter.FinalizePreparedWorldAsync(
                prepared,
                PreparedWorldDisposition.Discard));
        Assert.Contains("unrecognized 7 Days to Die Steward workspace", finalize.Message, StringComparison.OrdinalIgnoreCase);

        Assert.True(Directory.Exists(operationRoot));
        Assert.Equal("keep-me", await File.ReadAllTextAsync(sentinel));
    }

    private static PreparedWorld Prepared(string workingDirectory)
        => new(
            new GameInstallation("7-days-to-die:test", Path.GetTempPath(), "test"),
            workingDirectory,
            new EnvironmentManifest(
                1,
                "7-days-to-die",
                "test-build",
                [],
                new Dictionary<string, string>()));

    private static string GetExpectedWorkRoot()
    {
        var localData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localData))
        {
            localData = Path.GetTempPath();
        }

        return Path.Combine(localData, "Steward", "workspaces", "7-days-to-die");
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
