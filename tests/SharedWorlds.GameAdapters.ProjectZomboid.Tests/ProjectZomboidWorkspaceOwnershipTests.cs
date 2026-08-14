using SharedWorlds.Core.Abstractions;
using SharedWorlds.Core.Environment;

namespace SharedWorlds.GameAdapters.ProjectZomboid.Tests;

public sealed class ProjectZomboidWorkspaceOwnershipTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"sharedworlds-pz-ownership-{Guid.NewGuid():N}");
    private readonly List<string> _ownedWorkspaces = [];

    [Fact]
    public void CentralDisposableWorkspaceIsAcceptedAsExactRoot()
    {
        var workingDirectory = CreateOwnedWorkspace();

        var accepted = ProjectZomboidWorkspaceOwnership.RequireOwned(workingDirectory);

        Assert.Equal(Path.GetFullPath(workingDirectory), accepted);
        Assert.True(Guid.TryParseExact(
            Path.GetFileName(Path.TrimEndingDirectorySeparator(workingDirectory)),
            "N",
            out _));
        Assert.NotEqual(
            "Zomboid",
            Path.GetFileName(Path.TrimEndingDirectorySeparator(workingDirectory)));
    }

    [Fact]
    public void OwnedWorkspaceAcceptsContainedRegularPath()
    {
        var workingDirectory = CreateOwnedWorkspace();
        var contained = Path.Combine(workingDirectory, "Server", "steward.ini");
        Directory.CreateDirectory(Path.GetDirectoryName(contained)!);
        File.WriteAllText(contained, "DefaultPort=16261");

        var accepted = ProjectZomboidWorkspaceOwnership.RequireOwnedPath(
            workingDirectory,
            contained,
            "server config");

        Assert.Equal(Path.GetFullPath(contained), accepted);
    }

    [Fact]
    public async Task AdapterRefusesArbitraryRecoveryWorkspaceBeforeCaptureRestoreOrDelete()
    {
        var workingDirectory = Path.Combine(_root, "unowned-workspace");
        Directory.CreateDirectory(workingDirectory);
        var sentinel = Path.Combine(workingDirectory, "sentinel.txt");
        await File.WriteAllTextAsync(sentinel, "keep-me");
        var prepared = Prepared(workingDirectory);
        var adapter = new ProjectZomboidAdapter();

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

    [Fact]
    public void OwnedWorkspaceRejectsOutsidePath()
    {
        var workingDirectory = CreateOwnedWorkspace();
        var outside = Path.Combine(_root, "outside.ini");

        var exception = Assert.Throws<InvalidOperationException>(() =>
            ProjectZomboidWorkspaceOwnership.RequireOwnedPath(
                workingDirectory,
                outside,
                "server config"));

        Assert.Contains("escapes", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static PreparedWorld Prepared(string workingDirectory)
        => new(
            new GameInstallation("project-zomboid:test", Path.GetTempPath(), "test"),
            workingDirectory,
            new EnvironmentManifest(
                1,
                "project-zomboid",
                "test-build",
                [],
                new Dictionary<string, string>()));

    private string CreateOwnedWorkspace()
    {
        var workspace = ProjectZomboidWorkspaceOwnership.Create();
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
                    ProjectZomboidWorkspaceOwnership.DeleteOwned(workspace);
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
