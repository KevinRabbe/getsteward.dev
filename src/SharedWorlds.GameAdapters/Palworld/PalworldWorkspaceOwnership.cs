using SharedWorlds.Core.Abstractions;

namespace SharedWorlds.GameAdapters.Palworld;

internal static class PalworldWorkspaceOwnership
{
    private const string DedicatedServerNameKey = "dedicatedServerName";

    public static void RequireOwned(PreparedWorld world)
    {
        ArgumentNullException.ThrowIfNull(world);

        if (!world.Environment.Configuration.TryGetValue(DedicatedServerNameKey, out var worldId) ||
            string.IsNullOrWhiteSpace(worldId) ||
            !IsSafeWorldId(worldId))
        {
            throw Refuse(world.WorkingDirectory);
        }

        if (world.Installation.Metadata is null ||
            !world.Installation.Metadata.TryGetValue(
                PalworldInstallationDiscovery.DedicatedServerRootPathKey,
                out var serverRoot) ||
            string.IsNullOrWhiteSpace(serverRoot))
        {
            throw Refuse(world.WorkingDirectory);
        }

        var fullServerRoot = Path.GetFullPath(serverRoot);
        var expected = Path.GetFullPath(Path.Combine(
            fullServerRoot,
            "Pal",
            "Saved",
            "SaveGames",
            "0",
            worldId));
        var actual = Path.GetFullPath(world.WorkingDirectory);
        if (!string.Equals(expected, actual, PathComparison))
        {
            throw Refuse(world.WorkingDirectory);
        }

        RequireExistingRegularDirectoryChain(
            fullServerRoot,
            ["Pal", "Saved", "SaveGames", "0", worldId],
            world.WorkingDirectory);
    }

    private static void RequireExistingRegularDirectoryChain(
        string root,
        IReadOnlyList<string> relativeDirectories,
        string workingDirectory)
    {
        var current = root;
        if (!TryRequireRegularDirectory(current, workingDirectory))
        {
            return;
        }

        foreach (var segment in relativeDirectories)
        {
            current = Path.Combine(current, segment);
            if (!TryRequireRegularDirectory(current, workingDirectory))
            {
                return;
            }
        }
    }

    private static bool TryRequireRegularDirectory(string path, string workingDirectory)
    {
        FileAttributes attributes;
        try
        {
            attributes = File.GetAttributes(path);
        }
        catch (Exception exception) when (
            exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return false;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            throw RefuseUnsafePath(workingDirectory, path, exception);
        }

        if ((attributes & FileAttributes.ReparsePoint) != 0 ||
            (attributes & FileAttributes.Directory) == 0)
        {
            throw RefuseUnsafePath(workingDirectory, path);
        }

        return true;
    }

    private static bool IsSafeWorldId(string worldId)
        => !string.Equals(worldId, ".", StringComparison.Ordinal) &&
           !string.Equals(worldId, "..", StringComparison.Ordinal) &&
           !Path.IsPathRooted(worldId) &&
           string.Equals(Path.GetFileName(worldId), worldId, StringComparison.Ordinal) &&
           worldId.IndexOfAny(Path.GetInvalidFileNameChars()) < 0;

    private static StringComparison PathComparison => OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;

    private static InvalidOperationException Refuse(string workingDirectory)
        => new(
            $"Refusing to use unrecognized Palworld dedicated World path '{workingDirectory}'.");

    private static InvalidOperationException RefuseUnsafePath(
        string workingDirectory,
        string path,
        Exception? innerException = null)
        => new(
            $"Refusing to use Palworld dedicated World '{workingDirectory}' because ownership path '{path}' is linked/reparse or is not a regular directory.",
            innerException);
}
