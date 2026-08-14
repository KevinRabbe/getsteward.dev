using SharedWorlds.Core.Domain;

namespace SharedWorlds.Core.Storage;

/// <summary>
/// Owns the filesystem shape and safety checks for SafeWorld-managed recoverable workspaces.
/// The caller supplies the already-resolved workspace root; this type never discovers an OS
/// application-data directory or knows current/historical product directory names.
/// </summary>
public sealed class ManagedWorkspaceStorage
{
    private readonly string _root;

    public ManagedWorkspaceStorage(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        _root = Path.GetFullPath(root);
    }

    public string Root => _root;

    public string GetWorkspaceDirectory(WorkspaceId workspaceId, string adapterId)
    {
        var safeAdapterId = RequireSafePathSegment(adapterId, nameof(adapterId));
        return Path.GetFullPath(Path.Combine(
            _root,
            safeAdapterId,
            workspaceId.ToString()));
    }

    public string Create(WorkspaceId workspaceId, string adapterId)
    {
        var adapterRoot = GetAdapterRoot(adapterId);
        RejectReparsePointIfPresent(_root, _root);
        Directory.CreateDirectory(_root);
        RejectReparsePointIfPresent(_root, _root);

        RejectReparsePointIfPresent(adapterRoot, adapterRoot);
        Directory.CreateDirectory(adapterRoot);
        RejectReparsePointIfPresent(adapterRoot, adapterRoot);

        var workspace = GetWorkspaceDirectory(workspaceId, adapterId);
        if (Directory.Exists(workspace) || File.Exists(workspace))
        {
            throw new InvalidOperationException(
                $"Managed workspace '{workspaceId}' already exists for adapter '{adapterId}'.");
        }

        Directory.CreateDirectory(workspace);
        RejectReparsePointIfPresent(workspace, workspace);
        return workspace;
    }

    public string RequireOwned(
        WorkspaceId workspaceId,
        string adapterId,
        string workingDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);
        var expected = GetWorkspaceDirectory(workspaceId, adapterId);
        var actual = Path.GetFullPath(workingDirectory);
        if (!PathsEqual(expected, actual))
        {
            throw Refuse(workspaceId, adapterId, workingDirectory);
        }

        RejectReparsePointIfPresent(_root, workingDirectory);
        RejectReparsePointIfPresent(GetAdapterRoot(adapterId), workingDirectory);
        RejectReparsePointIfPresent(actual, workingDirectory);
        return actual;
    }

    public void RequireOwnedTree(
        WorkspaceId workspaceId,
        string adapterId,
        string workingDirectory)
    {
        var workspace = RequireOwned(workspaceId, adapterId, workingDirectory);
        if (!Directory.Exists(workspace))
        {
            return;
        }

        var pending = new Stack<string>();
        pending.Push(workspace);
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            RejectReparsePointIfPresent(current, workingDirectory);

            foreach (var directory in Directory.EnumerateDirectories(
                         current,
                         "*",
                         SearchOption.TopDirectoryOnly))
            {
                RejectReparsePointIfPresent(directory, workingDirectory);
                pending.Push(directory);
            }

            foreach (var file in Directory.EnumerateFiles(
                         current,
                         "*",
                         SearchOption.TopDirectoryOnly))
            {
                if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0)
                {
                    throw Refuse(workspaceId, adapterId, workingDirectory);
                }
            }
        }
    }

    public void DeleteOwned(
        WorkspaceId workspaceId,
        string adapterId,
        string workingDirectory)
    {
        RequireOwnedTree(workspaceId, adapterId, workingDirectory);
        if (Directory.Exists(workingDirectory))
        {
            Directory.Delete(workingDirectory, recursive: true);
        }
    }

    private string GetAdapterRoot(string adapterId)
        => Path.GetFullPath(Path.Combine(
            _root,
            RequireSafePathSegment(adapterId, nameof(adapterId))));

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

    private static void RejectReparsePointIfPresent(string path, string originalPath)
    {
        if (!Directory.Exists(path) && !File.Exists(path))
        {
            return;
        }

        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidOperationException(
                $"Refusing linked managed workspace path '{originalPath}'.");
        }
    }

    private static bool PathsEqual(string left, string right)
        => string.Equals(
            Path.GetFullPath(left),
            Path.GetFullPath(right),
            OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal);

    private static InvalidOperationException Refuse(
        WorkspaceId workspaceId,
        string adapterId,
        string path)
        => new(
            $"Refusing unrecognized managed workspace '{path}' for workspace '{workspaceId}' and adapter '{adapterId}'.");
}
