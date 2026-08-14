using SharedWorlds.Core.Abstractions;
using SharedWorlds.Core.Domain;

namespace SharedWorlds.GameAdapters.Factorio;

internal static class FactorioWorkspaceOwnership
{
    public static void RequireOwned(PreparedWorld world)
    {
        ArgumentNullException.ThrowIfNull(world);

        if (world.RecoveryLocation is { } location)
        {
            location.Validate();
            if (location.Kind != PreparedWorldRecoveryLocationKind.SafeWorldManaged)
            {
                throw Refuse(world.WorkingDirectory);
            }

            RequireRegularWorkspaceRoot(world.WorkingDirectory);
            return;
        }

        RequireLegacyScratch(world.WorkingDirectory);
    }

    /// <summary>
    /// Compatibility check for old no-context preparation only. Current writable workspaces must use
    /// the PreparedWorld overload so ownership comes from Core's recovery descriptor, not a path root.
    /// </summary>
    public static void RequireOwned(string workingDirectory)
        => RequireLegacyScratch(workingDirectory);

    public static void RequireOwnedPath(
        PreparedWorld world,
        string candidatePath,
        string description)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentException.ThrowIfNullOrWhiteSpace(candidatePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(description);
        RequireOwned(world);
        RequireContainedPath(world.WorkingDirectory, candidatePath, description);
    }

    public static void RequireOwnedPath(
        string workingDirectory,
        string candidatePath,
        string description)
    {
        RequireLegacyScratch(workingDirectory);
        RequireContainedPath(workingDirectory, candidatePath, description);
    }

    private static void RequireLegacyScratch(string workingDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);

        var fullPath = Path.GetFullPath(workingDirectory);
        var workspaceName = Path.GetFileName(Path.TrimEndingDirectorySeparator(fullPath));
        if (!Guid.TryParseExact(workspaceName, "N", out _))
        {
            throw Refuse(workingDirectory);
        }

        var ownerRoot = Directory.GetParent(fullPath)?.FullName;
        if (ownerRoot is null ||
            !PathsEqual(ownerRoot, FactorioWorldOperations.GetLegacyScratchRoot()))
        {
            throw Refuse(workingDirectory);
        }

        RequireRegularWorkspaceRoot(fullPath);
    }

    private static void RequireRegularWorkspaceRoot(string workingDirectory)
    {
        var fullPath = Path.GetFullPath(workingDirectory);
        if (Directory.Exists(fullPath) &&
            (File.GetAttributes(fullPath) & FileAttributes.ReparsePoint) != 0)
        {
            throw Refuse(workingDirectory);
        }
    }

    private static void RequireContainedPath(
        string workingDirectory,
        string candidatePath,
        string description)
    {
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

    private static bool PathsEqual(string left, string right)
        => string.Equals(
            Path.GetFullPath(left),
            Path.GetFullPath(right),
            OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal);

    private static InvalidOperationException Refuse(string workingDirectory)
        => new(
            $"Refusing to use unrecognized Factorio Safe World workspace '{workingDirectory}'.");

    private static InvalidOperationException RefusePath(
        string candidatePath,
        string description,
        Exception? innerException = null)
        => new(
            $"Refusing to use Factorio Safe World {description} path '{candidatePath}' because it is outside the prepared workspace or contains a linked/reparse path.",
            innerException);
}
