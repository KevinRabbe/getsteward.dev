using SharedWorlds.Core.Storage;

namespace SharedWorlds.GameAdapters.SevenDaysToDie;

internal static class SevenDaysToDieWorkspaceOwnership
{
    private const string AdapterId = "7-days-to-die";

    public static string Create()
        => DisposablePreparedWorkspaceStorage.Create(AdapterId);

    public static string RequireOwned(string workingDirectory)
        => DisposablePreparedWorkspaceStorage.RequireOwned(
            AdapterId,
            workingDirectory);

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
