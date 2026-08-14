namespace SharedWorlds.Core.Storage;

/// <summary>
/// Resolves process-local staging roots for captured state packages.
/// CapturedState packages are disposable by default and must not live in SafeWorld's durable
/// application-data tree or participate in durable-root migration/authority semantics.
/// </summary>
public static class DisposableStatePackageStorage
{
    private const string ProductDirectoryName = "SafeWorld";
    private const string StagingDirectoryName = "state-package-staging";

    public static string GetAdapterRoot(string adapterId)
    {
        RequireSafePathSegment(adapterId, nameof(adapterId));

        return Path.GetFullPath(Path.Combine(
            Path.GetTempPath(),
            ProductDirectoryName,
            StagingDirectoryName,
            adapterId));
    }

    private static void RequireSafePathSegment(string value, string parameterName)
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
    }
}
