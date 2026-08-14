namespace SharedWorlds.Core.Storage;

/// <summary>
/// Owns process-local staging locations for captured state packages.
/// CapturedState packages are disposable by default and therefore must never participate in
/// SafeWorld's durable application-data migration, authority, or recovery semantics.
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

    /// <summary>
    /// Allocates a unique path for a disposable captured-state package. The caller owns creating
    /// the file and CapturedState owns deleting it after durable storage unless it explicitly opts out.
    /// </summary>
    public static string CreatePackagePath(
        string adapterId,
        string? descriptiveName,
        string extension)
    {
        RequireSafePathSegment(adapterId, nameof(adapterId));
        var normalizedExtension = RequireSafeExtension(extension);
        var root = GetAdapterRoot(adapterId);
        Directory.CreateDirectory(root);

        var safeName = SanitizeFileName(descriptiveName);
        return Path.Combine(
            root,
            $"{safeName}-{Guid.NewGuid():N}{normalizedExtension}");
    }

    private static string SanitizeFileName(string? value)
    {
        var result = string.IsNullOrWhiteSpace(value)
            ? "world"
            : value.Trim();

        foreach (var invalidCharacter in Path.GetInvalidFileNameChars())
        {
            result = result.Replace(invalidCharacter, '_');
        }

        result = result.TrimEnd('.', ' ');
        return string.IsNullOrWhiteSpace(result)
            ? "world"
            : result;
    }

    private static string RequireSafeExtension(string extension)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(extension);
        var normalized = extension.StartsWith('.', StringComparison.Ordinal)
            ? extension
            : $".{extension}";

        if (normalized.Length < 2 ||
            normalized.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            normalized.Contains(Path.DirectorySeparatorChar) ||
            normalized.Contains(Path.AltDirectorySeparatorChar) ||
            normalized[1..].Contains('.', StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "Package extension must be one safe filename extension.",
                nameof(extension));
        }

        return normalized;
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
