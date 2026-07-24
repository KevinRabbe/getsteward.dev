namespace SharedWorlds.GameAdapters.SevenDaysToDie;

internal static class SevenDaysToDieWorkspaceOwnership
{
    private const string WorkspaceLeafName = "user-data";

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
            "7-days-to-die"));
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
            $"Refusing to use unrecognized 7 Days to Die Steward workspace '{workingDirectory}'.");
}
