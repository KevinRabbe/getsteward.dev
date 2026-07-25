namespace SharedWorlds.GameAdapters.ProjectZomboid;

internal static class ProjectZomboidWorkspaceOwnership
{
    private const string WorkspaceLeafName = "Zomboid";

    public static void RequireOwned(string workingDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);

        var workspace = Path.GetFullPath(workingDirectory);
        if (!string.Equals(
                Path.GetFileName(Path.TrimEndingDirectorySeparator(workspace)),
                WorkspaceLeafName,
                PathComparison))
        {
            throw Refuse(workingDirectory);
        }

        var operationRoot = Directory.GetParent(workspace)?.FullName;
        if (operationRoot is null ||
            !Guid.TryParseExact(
                Path.GetFileName(Path.TrimEndingDirectorySeparator(operationRoot)),
                "N",
                out _))
        {
            throw Refuse(workingDirectory);
        }

        var ownerRoot = Directory.GetParent(operationRoot)?.FullName;
        if (ownerRoot is null || !PathsEqual(ownerRoot, GetExpectedWorkRoot()))
        {
            throw Refuse(workingDirectory);
        }

        RejectReparsePoint(operationRoot, workingDirectory);
        if (Directory.Exists(workspace))
        {
            RejectReparsePoint(workspace, workingDirectory);
        }
    }

    public static void RequireOwnedPath(
        string workingDirectory,
        string candidatePath,
        string description)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(candidatePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(description);
        RequireOwned(workingDirectory);

        var workspace = Path.GetFullPath(workingDirectory);
        var candidate = Path.GetFullPath(candidatePath);
        var relative = Path.GetRelativePath(workspace, candidate);
        if (!IsContainedRelativePath(relative))
        {
            throw RefusePath(candidatePath, description);
        }

        if (string.Equals(relative, ".", StringComparison.Ordinal))
        {
            return;
        }

        var current = workspace;
        foreach (var segment in relative.Split(
                     new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);

            FileAttributes attributes;
            try
            {
                attributes = File.GetAttributes(current);
            }
            catch (FileNotFoundException)
            {
                return;
            }
            catch (DirectoryNotFoundException)
            {
                return;
            }
            catch (IOException exception)
            {
                throw RefusePath(candidatePath, description, exception);
            }
            catch (UnauthorizedAccessException exception)
            {
                throw RefusePath(candidatePath, description, exception);
            }

            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw RefusePath(candidatePath, description);
            }
        }
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

    private static string GetExpectedWorkRoot()
    {
        var localData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localData))
        {
            localData = Path.GetTempPath();
        }

        return Path.GetFullPath(Path.Combine(
            localData,
            "Steward",
            "workspaces",
            "project-zomboid"));
    }

    private static void RejectReparsePoint(string path, string originalPath)
    {
        if (!Directory.Exists(path))
        {
            return;
        }

        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw Refuse(originalPath);
        }
    }

    private static bool PathsEqual(string left, string right)
        => string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), PathComparison);

    private static StringComparison PathComparison => OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;

    private static InvalidOperationException Refuse(string workingDirectory)
        => new(
            $"Refusing to use unrecognized Project Zomboid Steward workspace '{workingDirectory}'.");

    private static InvalidOperationException RefusePath(
        string candidatePath,
        string description,
        Exception? innerException = null)
        => new(
            $"Refusing to use Project Zomboid Steward {description} path '{candidatePath}' because it is outside the owned workspace or contains a linked/reparse path.",
            innerException);
}
