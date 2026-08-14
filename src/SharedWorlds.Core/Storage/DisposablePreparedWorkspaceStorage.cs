namespace SharedWorlds.Core.Storage;

/// <summary>
/// Owns temporary prepared-workspace scratch for compatibility/non-writable adapter operations that
/// do not participate in durable recovery. These workspaces are process-local disposable state and
/// must never be placed under SafeWorld's durable application-data root.
/// </summary>
public static class DisposablePreparedWorkspaceStorage
{
    private const string ProductDirectoryName = "SafeWorld";
    private const string ScratchDirectoryName = "prepared-workspace-scratch";

    public static string Create(string adapterId)
    {
        var productRoot = GetProductRoot();
        RejectReparsePointIfPresent(productRoot, productRoot);
        Directory.CreateDirectory(productRoot);
        RejectReparsePointIfPresent(productRoot, productRoot);

        var scratchRoot = GetScratchRoot();
        RejectReparsePointIfPresent(scratchRoot, scratchRoot);
        Directory.CreateDirectory(scratchRoot);
        RejectReparsePointIfPresent(scratchRoot, scratchRoot);

        var adapterRoot = GetAdapterRoot(adapterId);
        RejectReparsePointIfPresent(adapterRoot, adapterRoot);
        Directory.CreateDirectory(adapterRoot);
        RejectReparsePointIfPresent(adapterRoot, adapterRoot);

        var workspace = Path.Combine(adapterRoot, Guid.NewGuid().ToString("N"));
        if (Directory.Exists(workspace) || File.Exists(workspace))
        {
            throw new InvalidOperationException(
                $"Disposable prepared workspace already exists unexpectedly: {workspace}");
        }

        Directory.CreateDirectory(workspace);
        RejectReparsePointIfPresent(workspace, workspace);
        return Path.GetFullPath(workspace);
    }

    public static string RequireOwned(string adapterId, string workingDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);
        var expectedRoot = GetAdapterRoot(adapterId);
        var workspace = Path.GetFullPath(workingDirectory);
        var parent = Directory.GetParent(workspace)?.FullName;
        var leaf = Path.GetFileName(Path.TrimEndingDirectorySeparator(workspace));
        if (parent is null ||
            !PathsEqual(parent, expectedRoot) ||
            !Guid.TryParseExact(leaf, "N", out _))
        {
            throw Refuse(adapterId, workingDirectory);
        }

        RejectReparsePointIfPresent(GetProductRoot(), workingDirectory);
        RejectReparsePointIfPresent(GetScratchRoot(), workingDirectory);
        RejectReparsePointIfPresent(expectedRoot, workingDirectory);
        RejectReparsePointIfPresent(workspace, workingDirectory);
        return workspace;
    }

    public static void RequireOwnedTree(
        string adapterId,
        string workingDirectory,
        string treeRoot)
    {
        var workspace = RequireOwned(adapterId, workingDirectory);
        var root = Path.GetFullPath(treeRoot);
        var relative = Path.GetRelativePath(workspace, root);
        if (!IsContainedRelativePath(relative))
        {
            throw Refuse(adapterId, treeRoot);
        }

        if (!Directory.Exists(root))
        {
            return;
        }

        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            RejectReparsePointIfPresent(current, treeRoot);

            foreach (var directory in Directory.EnumerateDirectories(
                         current,
                         "*",
                         SearchOption.TopDirectoryOnly))
            {
                RejectReparsePointIfPresent(directory, treeRoot);
                pending.Push(directory);
            }

            foreach (var file in Directory.EnumerateFiles(
                         current,
                         "*",
                         SearchOption.TopDirectoryOnly))
            {
                if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0)
                {
                    throw Refuse(adapterId, treeRoot);
                }
            }
        }
    }

    public static void DeleteOwned(string adapterId, string workingDirectory)
    {
        RequireOwnedTree(adapterId, workingDirectory, workingDirectory);
        if (Directory.Exists(workingDirectory))
        {
            Directory.Delete(workingDirectory, recursive: true);
        }
    }

    private static string GetAdapterRoot(string adapterId)
        => Path.GetFullPath(Path.Combine(
            GetScratchRoot(),
            RequireSafePathSegment(adapterId, nameof(adapterId))));

    private static string GetProductRoot()
        => Path.GetFullPath(Path.Combine(
            Path.GetTempPath(),
            ProductDirectoryName));

    private static string GetScratchRoot()
        => Path.GetFullPath(Path.Combine(
            GetProductRoot(),
            ScratchDirectoryName));

    private static string RequireSafePathSegment(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        if (string.Equals(value, ".", StringComparison.Ordinal) ||
            string.Equals(value, "..", StringComparison.Ordinal) ||
            Path.IsPathRooted(value) ||
            !string.Equals(Path.GetFileName(value), value, StringComparison.Ordinal) ||
            value.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            throw new ArgumentException(
                "Adapter id must be a single safe filesystem path segment.",
                parameterName);
        }

        return value;
    }

    private static bool IsContainedRelativePath(string relativePath)
    {
        if (Path.IsPathRooted(relativePath) ||
            string.Equals(relativePath, "..", StringComparison.Ordinal))
        {
            return false;
        }

        return !relativePath.StartsWith(
                   $"..{Path.DirectorySeparatorChar}",
                   StringComparison.Ordinal) &&
               !relativePath.StartsWith(
                   $"..{Path.AltDirectorySeparatorChar}",
                   StringComparison.Ordinal);
    }

    private static void RejectReparsePointIfPresent(string path, string originalPath)
    {
        if (!Directory.Exists(path) && !File.Exists(path))
        {
            return;
        }

        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidOperationException(
                $"Refusing linked disposable prepared workspace path '{originalPath}'.");
        }
    }

    private static bool PathsEqual(string left, string right)
        => string.Equals(
            Path.GetFullPath(left),
            Path.GetFullPath(right),
            OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal);

    private static InvalidOperationException Refuse(string adapterId, string path)
        => new(
            $"Refusing unrecognized or linked disposable prepared workspace '{path}' for adapter '{adapterId}'.");
}
