using SharedWorlds.Core.Storage;

namespace SharedWorlds.Core.Tests;

public sealed class DisposablePreparedWorkspaceStorageTests
{
    [Fact]
    public void CreateUsesTempScratchAndOwnedDeletionRemovesOnlyExactWorkspace()
    {
        var workspace = DisposablePreparedWorkspaceStorage.Create("scratch-test");
        try
        {
            Assert.StartsWith(
                Path.GetFullPath(Path.GetTempPath()),
                Path.GetFullPath(workspace),
                OperatingSystem.IsWindows()
                    ? StringComparison.OrdinalIgnoreCase
                    : StringComparison.Ordinal);
            Assert.Equal(
                Path.GetFullPath(workspace),
                DisposablePreparedWorkspaceStorage.RequireOwned(
                    "scratch-test",
                    workspace));

            File.WriteAllText(Path.Combine(workspace, "state.txt"), "state");
            DisposablePreparedWorkspaceStorage.DeleteOwned("scratch-test", workspace);
            Assert.False(Directory.Exists(workspace));
        }
        finally
        {
            if (Directory.Exists(workspace))
            {
                Directory.Delete(workspace, recursive: true);
            }
        }
    }

    [Fact]
    public void DifferentAdapterOrArbitraryPathIsNeverAcceptedAsOwnedScratch()
    {
        var workspace = DisposablePreparedWorkspaceStorage.Create("scratch-a");
        var arbitrary = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(arbitrary);
        try
        {
            Assert.Throws<InvalidOperationException>(() =>
                DisposablePreparedWorkspaceStorage.RequireOwned("scratch-b", workspace));
            Assert.Throws<InvalidOperationException>(() =>
                DisposablePreparedWorkspaceStorage.RequireOwned("scratch-a", arbitrary));
        }
        finally
        {
            DisposablePreparedWorkspaceStorage.DeleteOwned("scratch-a", workspace);
            Directory.Delete(arbitrary, recursive: true);
        }
    }

    [Fact]
    public void OwnedTreeCannotEscapeWorkspace()
    {
        var workspace = DisposablePreparedWorkspaceStorage.Create("scratch-tree");
        var outside = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outside);
        try
        {
            Assert.Throws<InvalidOperationException>(() =>
                DisposablePreparedWorkspaceStorage.RequireOwnedTree(
                    "scratch-tree",
                    workspace,
                    outside));
        }
        finally
        {
            DisposablePreparedWorkspaceStorage.DeleteOwned("scratch-tree", workspace);
            Directory.Delete(outside, recursive: true);
        }
    }

    [Fact]
    public void FileCandidateCannotTraverseLinkedWorkspaceAncestor()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var workspace = DisposablePreparedWorkspaceStorage.Create("scratch-linked-ancestor");
        var outside = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outside);
        var linkedDirectory = Path.Combine(workspace, "Server");
        Directory.CreateSymbolicLink(linkedDirectory, outside);
        try
        {
            var existingCandidate = Path.Combine(linkedDirectory, "server.ini");
            File.WriteAllText(Path.Combine(outside, "server.ini"), "state");
            Assert.Throws<InvalidOperationException>(() =>
                DisposablePreparedWorkspaceStorage.RequireOwnedTree(
                    "scratch-linked-ancestor",
                    workspace,
                    existingCandidate));

            var futureCandidate = Path.Combine(linkedDirectory, "future.tmp");
            Assert.Throws<InvalidOperationException>(() =>
                DisposablePreparedWorkspaceStorage.RequireOwnedTree(
                    "scratch-linked-ancestor",
                    workspace,
                    futureCandidate));
        }
        finally
        {
            Directory.Delete(linkedDirectory);
            DisposablePreparedWorkspaceStorage.DeleteOwned("scratch-linked-ancestor", workspace);
            Directory.Delete(outside, recursive: true);
        }
    }
}
