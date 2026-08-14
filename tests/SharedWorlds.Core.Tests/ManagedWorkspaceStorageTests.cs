using SharedWorlds.Core.Domain;
using SharedWorlds.Core.Storage;

namespace SharedWorlds.Core.Tests;

public sealed class ManagedWorkspaceStorageTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"safeworld-managed-workspace-tests-{Guid.NewGuid():N}");

    [Fact]
    public void WorkspacePathIsDerivedFromStableIdentityAndAdapter()
    {
        var storage = new ManagedWorkspaceStorage(_root);
        var workspaceId = WorkspaceId.New();

        var path = storage.GetWorkspaceDirectory(workspaceId, "factorio");

        Assert.Equal(
            Path.GetFullPath(Path.Combine(_root, "factorio", workspaceId.ToString())),
            path);
    }

    [Fact]
    public void CreateAndDeleteRequireExactOwnedWorkspace()
    {
        var storage = new ManagedWorkspaceStorage(_root);
        var workspaceId = WorkspaceId.New();
        var path = storage.Create(workspaceId, "factorio");
        File.WriteAllText(Path.Combine(path, "state.txt"), "recoverable");

        Assert.Equal(path, storage.RequireOwned(workspaceId, "factorio", path));

        storage.DeleteOwned(workspaceId, "factorio", path);

        Assert.False(Directory.Exists(path));
    }

    [Fact]
    public void WrongWorkspacePathFailsClosed()
    {
        var storage = new ManagedWorkspaceStorage(_root);
        var workspaceId = WorkspaceId.New();
        var path = storage.Create(workspaceId, "factorio");
        var other = Path.Combine(_root, "factorio", WorkspaceId.New().ToString());

        Assert.Throws<InvalidOperationException>(() =>
            storage.RequireOwned(workspaceId, "factorio", other));
        Assert.True(Directory.Exists(path));
    }

    [Theory]
    [InlineData("../factorio")]
    [InlineData("factorio/child")]
    [InlineData(".")]
    [InlineData("..")]
    public void AdapterIdMustBeOneSafePathSegment(string adapterId)
    {
        var storage = new ManagedWorkspaceStorage(_root);

        Assert.Throws<ArgumentException>(() =>
            storage.GetWorkspaceDirectory(WorkspaceId.New(), adapterId));
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
