namespace SharedWorlds.GameAdapters.Palworld;

internal static class PalworldDedicatedRuntimeInputSafety
{
    public static void RequireRegularDirectory(string path, string description)
    {
        if (!TryRequireRegularDirectory(path, description))
        {
            throw new InvalidOperationException($"{description} was not found: {path}");
        }
    }

    public static void RequireRegularFile(string path, string description)
    {
        if (!TryRequireRegularFile(path, description))
        {
            throw new InvalidOperationException($"{description} was not found: {path}");
        }
    }

    public static bool TryRequireRegularFile(string path, string description)
        => TryRequireRegularPath(path, description, expectDirectory: false);

    public static bool TryGetRegularFileUnderRoot(
        string root,
        IReadOnlyList<string> relativeDirectories,
        string fileName,
        string description,
        out string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        ArgumentNullException.ThrowIfNull(relativeDirectories);
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);

        var current = Path.GetFullPath(root);
        RequireRegularDirectory(current, "Palworld dedicated-server root");
        foreach (var segment in relativeDirectories)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(segment);
            current = Path.Combine(current, segment);
            if (!TryRequireRegularDirectory(current, $"Palworld dedicated-server '{segment}' directory"))
            {
                path = Path.Combine(current, fileName);
                return false;
            }
        }

        path = Path.Combine(current, fileName);
        return TryRequireRegularFile(path, description);
    }

    public static string EnsureRegularDirectoryChain(
        string root,
        params string[] relativeDirectories)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        ArgumentNullException.ThrowIfNull(relativeDirectories);

        var current = Path.GetFullPath(root);
        RequireRegularDirectory(current, "Palworld dedicated-server root");
        foreach (var segment in relativeDirectories)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(segment);
            current = Path.Combine(current, segment);
            if (!TryRequireRegularDirectory(current, $"Palworld dedicated-server '{segment}' directory"))
            {
                Directory.CreateDirectory(current);
                RequireRegularDirectory(current, $"Palworld dedicated-server '{segment}' directory");
            }
        }

        return current;
    }

    public static void RequireAbsentOrRegularDirectory(string path, string description)
    {
        try
        {
            var attributes = File.GetAttributes(path);
            ValidateRegularAttributes(path, description, expectDirectory: true, attributes);
        }
        catch (Exception exception) when (
            exception is FileNotFoundException or DirectoryNotFoundException)
        {
            // Absence is allowed when the caller is about to materialize this directory under a
            // previously validated regular parent.
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException(
                $"{description} could not be inspected safely: {path}: {exception.Message}",
                exception);
        }
    }

    private static bool TryRequireRegularDirectory(string path, string description)
        => TryRequireRegularPath(path, description, expectDirectory: true);

    private static bool TryRequireRegularPath(
        string path,
        string description,
        bool expectDirectory)
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
            throw new InvalidOperationException(
                $"{description} could not be inspected safely: {path}: {exception.Message}",
                exception);
        }

        ValidateRegularAttributes(path, description, expectDirectory, attributes);
        return true;
    }

    private static void ValidateRegularAttributes(
        string path,
        string description,
        bool expectDirectory,
        FileAttributes attributes)
    {
        if ((attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidOperationException(
                $"{description} is linked or a reparse point. Steward will not use bytes outside the Palworld dedicated runtime: {path}");
        }

        var isDirectory = (attributes & FileAttributes.Directory) != 0;
        if (isDirectory != expectDirectory)
        {
            throw new InvalidOperationException(
                $"{description} is not a {(expectDirectory ? "directory" : "regular file")}: {path}");
        }
    }
}
