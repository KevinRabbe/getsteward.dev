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
}
