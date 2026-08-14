using SharedWorlds.Core.Storage;

namespace SharedWorlds.GameAdapters.ProjectZomboid;

internal static class ProjectZomboidWorkspaceOwnership
{
    private const string AdapterId = "project-zomboid";

    public static string Create()
        => DisposablePreparedWorkspaceStorage.Create(AdapterId);

    public static string RequireOwned(string workingDirectory)
        => DisposablePreparedWorkspaceStorage.RequireOwned(
            AdapterId,
            workingDirectory);

    public static string RequireOwnedPath(
        string workingDirectory,
        string candidatePath,
        string description)
    {
        var workspace = RequireOwned(workingDirectory);
        var candidate = Path.GetFullPath(candidatePath);
        try
        {
            DisposablePreparedWorkspaceStorage.RequireOwnedTree(
                AdapterId,
                workspace,
                candidate);
        }
        catch (InvalidOperationException exception)
        {
            throw new InvalidOperationException(
                $"Project Zomboid {description} escapes or links outside the owned SafeWorld workspace: '{candidatePath}'.",
                exception);
        }

        return candidate;
    }

    public static void RequireOwnedTree(string workingDirectory)
        => DisposablePreparedWorkspaceStorage.RequireOwnedTree(
            AdapterId,
            workingDirectory,
            workingDirectory);

    public static void DeleteOwned(string workingDirectory)
        => DisposablePreparedWorkspaceStorage.DeleteOwned(
            AdapterId,
            workingDirectory);
}
