using SharedWorlds.Core.Abstractions;
using SharedWorlds.Core.Domain;
using SharedWorlds.Core.Storage;

namespace SharedWorlds.GameAdapters.Terraria;

internal static class TerrariaWorkspaceOwnership
{
    private const string AdapterId = "terraria";

    /// <summary>
    /// Compatibility scratch for no-context/non-writable preparation only. Writable sessions receive
    /// an identity-bound managed workspace from Core before adapter materialization begins.
    /// </summary>
    public static string Create()
        => DisposablePreparedWorkspaceStorage.Create(AdapterId);

    public static void RequireOwned(PreparedWorld world)
    {
        ArgumentNullException.ThrowIfNull(world);
        if (world.RecoveryLocation is not { } location)
        {
            RequireOwned(world.WorkingDirectory);
            return;
        }

        location.Validate();
        if (location.Kind != PreparedWorldRecoveryLocationKind.SafeWorldManaged)
        {
            throw Refuse(world.WorkingDirectory);
        }

        RequireRegularManagedTree(world.WorkingDirectory);
    }

    public static void RequireOwned(string workingDirectory)
        => DisposablePreparedWorkspaceStorage.RequireOwned(AdapterId, workingDirectory);

    public static void DeleteOwned(PreparedWorld world)
    {
        RequireOwned(world);
        if (!Directory.Exists(world.WorkingDirectory))
        {
            return;
        }

        if (world.RecoveryLocation is null)
        {
            DisposablePreparedWorkspaceStorage.DeleteOwned(AdapterId, world.WorkingDirectory);
            return;
        }

        // Transitional compatibility: normal lifecycle finalization is being moved into Core. Until
        // every caller uses that coordinator, migrated managed workspaces still require a safe local
        // delete here. Core independently proves exact WorkspaceId ownership in recovery cleanup.
        RequireRegularManagedTree(world.WorkingDirectory);
        Directory.Delete(world.WorkingDirectory, recursive: true);
    }

    private static void RequireRegularManagedTree(string workingDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);
        var root = Path.GetFullPath(workingDirectory);
        if (!Directory.Exists(root))
        {
            return;
        }

        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            RejectReparsePoint(current, workingDirectory);
            foreach (var directory in Directory.EnumerateDirectories(
                         current,
                         "*",
                         SearchOption.TopDirectoryOnly))
            {
                RejectReparsePoint(directory, workingDirectory);
                pending.Push(directory);
            }

            foreach (var file in Directory.EnumerateFiles(
                         current,
                         "*",
                         SearchOption.TopDirectoryOnly))
            {
                if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0)
                {
                    throw Refuse(workingDirectory);
                }
            }
        }
    }

    private static void RejectReparsePoint(string path, string originalPath)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw Refuse(originalPath);
        }
    }

    private static InvalidOperationException Refuse(string workingDirectory)
        => new($"Refusing to use unrecognized or linked Terraria Safe World workspace '{workingDirectory}'.");
}
