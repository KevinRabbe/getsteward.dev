using SharedWorlds.Core.Abstractions;
using SharedWorlds.Core.Environment;

namespace SharedWorlds.GameAdapters.SevenDaysToDie.Tests;

public sealed class SevenDaysToDieWorkspaceOwnershipTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"sharedworlds-7dtd-ownership-{Guid.NewGuid():N}");
    private readonly List<string> _ownedWorkspaces = [];

    [Fact]
    public void CentralDisposableWorkspaceIsAcceptedAsExactRoot()
    {
        var workingDirectory = CreateOwnedWorkspace();

        var accepted = SevenDaysToDieWorkspaceOwnership.RequireOwned(workingDirectory);

        Assert.Equal(Path.GetFullPath(workingDirectory), accepted);
        Assert.True(Guid.TryParseExact(
            Path.GetFileName(Path.TrimEndingDirectorySeparator(workingDirectory)),
            "N",
            out _));
        Assert.NotEqual(
            "user-data",
            Path.GetFileName(Path.TrimEndingDirectorySeparator(workingDirectory)));
    }

    [Fact]
    public async Task AdapterRefusesArbitraryRecoveryWorkspaceBeforeCaptureRestoreOrDelete()
    {
        var workingDirectory = Path.Combine(_root, "unowned-workspace");
        Directory.CreateDirectory(workingDirectory);
        var sentinel = Path.Combine(workingDirectory, "sentinel.txt");
        await File.WriteAllTextAsync(sentinel, "keep-me");
        var prepared = Prepared(workingDirectory);
        var adapter = new SevenDaysToDieAdapter();

        var capture = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            adapter.CaptureStateAsync(prepared));
        Assert.Contains("disposable prepared workspace", capture.Message, StringComparison.OrdinalIgnoreCase);

        var restore = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            adapter.RestoreStateAsync(
                prepared,
                new StatePackage("missing", Path.Combine(_root, "missing.zip"))));
        Assert.Contains("disposable prepared workspace", restore.Message, StringComparison.OrdinalIgnoreCase);

        var finalize = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            adapter.FinalizePreparedWorldAsync(
                prepared,
                PreparedWorldDisposition.Discard));
        Assert.Contains("disposable prepared workspace", finalize.Message, StringComparison.OrdinalIgnoreCase);

        Assert.True(Directory.Exists(workingDirectory));
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

    private string CreateOwnedWorkspace()
    {
        var workspace = SevenDaysToDieWorkspaceOwnership.Create();
        _ownedWorkspaces.Add(workspace);
        return workspace;
    }

    public void Dispose()
    {
        foreach (var workspace in _ownedWorkspaces)
        {
            try
            {
                if (Directory.Exists(workspace))
                {
                    SevenDaysToDieWorkspaceOwnership.DeleteOwned(workspace);
                }
            }
            catch
            {
            }
        }

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
