namespace SharedWorlds.GameAdapters.Factorio;

internal static class FactorioWorkspaceOwnership
{
    public static void RequireOwned(string workingDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);

        var fullPath = Path.GetFullPath(workingDirectory);
        var workspaceName = Path.GetFileName(Path.TrimEndingDirectorySeparator(fullPath));
        if (!Guid.TryParseExact(workspaceName, "N", out _))
        {
            throw Refuse(workingDirectory);
        }

        var ownerRoot = Directory.GetParent(fullPath)?.FullName;
        if (ownerRoot is null || !PathsEqual(ownerRoot, GetExpectedWorkRoot()))
        {
            throw Refuse(workingDirectory);
        }

        if (Directory.Exists(fullPath) &&
            (File.GetAttributes(fullPath) & FileAttributes.ReparsePoint) != 0)
        {
            throw Refuse(workingDirectory);
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
        if (Path.IsPathRooted(relativePath))
        {
            return false;
        }

        if (string.Equals(relativePath, "..", StringComparison.Ordinal))
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
        var basePath = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(basePath))
        {
            basePath = Path.GetTempPath();
        }

        return Path.GetFullPath(Path.Combine(basePath, "SharedWorlds", "factorio"));
    }

    private static bool PathsEqual(string left, string right)
        => string.Equals(
            Path.GetFullPath(left),
            Path.GetFullPath(right),
            OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal);

    private static InvalidOperationException Refuse(string workingDirectory)
        => new(
            $"Refusing to use unrecognized Factorio Steward workspace '{workingDirectory}'.");

    private static InvalidOperationException RefusePath(
        string candidatePath,
        string description,
        Exception? innerException = null)
        => new(
            $"Refusing to use Factorio Steward {description} path '{candidatePath}' because it is outside the owned workspace or contains a linked/reparse path.",
            innerException);
}
