using SharedWorlds.Core.Abstractions;
using SharedWorlds.Core.Environment;

namespace SharedWorlds.GameAdapters.Factorio;

internal static class FactorioEnvironmentVerifier
{
    public static async Task<EnvironmentVerificationReport> VerifyAsync(
        GameInstallation installation,
        EnvironmentManifest requiredEnvironment,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(installation);
        ArgumentNullException.ThrowIfNull(requiredEnvironment);

        if (!string.Equals(requiredEnvironment.AdapterId, "factorio", StringComparison.Ordinal))
        {
            return EnvironmentVerificationReport.Blocked(
                new EnvironmentVerificationIssue(
                    "factorio-adapter-mismatch",
                    $"The World requires adapter '{requiredEnvironment.AdapterId}', not Factorio."));
        }

        EnvironmentManifest actual;
        try
        {
            actual = await FactorioEnvironmentInspector.InspectAsync(
                installation,
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            return EnvironmentVerificationReport.Blocked(
                new EnvironmentVerificationIssue(
                    "factorio-environment-inspection-failed",
                    $"Steward could not inspect the installed Factorio environment: {exception.Message}"));
        }

        var issues = new List<EnvironmentVerificationIssue>();
        if (actual.SchemaVersion != requiredEnvironment.SchemaVersion)
        {
            issues.Add(new EnvironmentVerificationIssue(
                "factorio-manifest-schema-mismatch",
                $"Environment manifest schema is {actual.SchemaVersion}; the World requires {requiredEnvironment.SchemaVersion}."));
        }

        if (!string.Equals(actual.GameVersion, requiredEnvironment.GameVersion, StringComparison.Ordinal))
        {
            issues.Add(new EnvironmentVerificationIssue(
                "factorio-game-version-mismatch",
                $"Installed Factorio version is '{actual.GameVersion}'; the World requires exact version '{requiredEnvironment.GameVersion}'."));
        }

        CompareComponents(requiredEnvironment.Components, actual.Components, issues);
        CompareConfiguration(requiredEnvironment.Configuration, actual.Configuration, issues);

        return issues.Count == 0
            ? EnvironmentVerificationReport.Ready()
            : new EnvironmentVerificationReport(EnvironmentVerificationState.Blocked, issues);
    }

    private static void CompareComponents(
        IReadOnlyList<EnvironmentComponent> required,
        IReadOnlyList<EnvironmentComponent> actual,
        ICollection<EnvironmentVerificationIssue> issues)
    {
        var actualByKey = actual.ToDictionary(ComponentKey, StringComparer.Ordinal);
        foreach (var expected in required)
        {
            var key = ComponentKey(expected);
            if (!actualByKey.TryGetValue(key, out var installed))
            {
                issues.Add(new EnvironmentVerificationIssue(
                    "factorio-component-missing",
                    $"Required {expected.Kind} '{expected.Id}' is not enabled."));
                continue;
            }

            if (!string.Equals(installed.Version, expected.Version, StringComparison.Ordinal))
            {
                issues.Add(new EnvironmentVerificationIssue(
                    "factorio-component-version-mismatch",
                    $"{expected.Kind} '{expected.Id}' is version '{installed.Version ?? "unknown"}'; the World requires '{expected.Version ?? "unknown"}'."));
            }

            if (!string.Equals(installed.Source, expected.Source, StringComparison.Ordinal))
            {
                issues.Add(new EnvironmentVerificationIssue(
                    "factorio-component-source-mismatch",
                    $"{expected.Kind} '{expected.Id}' comes from '{installed.Source}'; the World requires source '{expected.Source}'."));
            }

            if (!SameDictionary(installed.Metadata, expected.Metadata))
            {
                issues.Add(new EnvironmentVerificationIssue(
                    "factorio-component-metadata-mismatch",
                    $"{expected.Kind} '{expected.Id}' metadata does not match the World environment."));
            }
        }

        var requiredKeys = required.Select(ComponentKey).ToHashSet(StringComparer.Ordinal);
        foreach (var installed in actual)
        {
            if (!requiredKeys.Contains(ComponentKey(installed)))
            {
                issues.Add(new EnvironmentVerificationIssue(
                    "factorio-unexpected-component",
                    $"Unexpected enabled {installed.Kind} '{installed.Id}' is not part of the World environment."));
            }
        }
    }

    private static void CompareConfiguration(
        IReadOnlyDictionary<string, string> required,
        IReadOnlyDictionary<string, string> actual,
        ICollection<EnvironmentVerificationIssue> issues)
    {
        foreach (var expected in required)
        {
            if (!actual.TryGetValue(expected.Key, out var value))
            {
                issues.Add(new EnvironmentVerificationIssue(
                    "factorio-configuration-missing",
                    $"Required Factorio configuration '{expected.Key}' is missing."));
            }
            else if (!string.Equals(value, expected.Value, StringComparison.Ordinal))
            {
                issues.Add(new EnvironmentVerificationIssue(
                    "factorio-configuration-mismatch",
                    $"Factorio configuration '{expected.Key}' does not match the World environment."));
            }
        }

        foreach (var actualEntry in actual)
        {
            if (!required.ContainsKey(actualEntry.Key))
            {
                issues.Add(new EnvironmentVerificationIssue(
                    "factorio-unexpected-configuration",
                    $"Unexpected Factorio configuration '{actualEntry.Key}' is present."));
            }
        }
    }

    private static string ComponentKey(EnvironmentComponent component)
        => $"{component.Kind}\u001f{component.Id}";

    private static bool SameDictionary(
        IReadOnlyDictionary<string, string>? left,
        IReadOnlyDictionary<string, string>? right)
    {
        if (ReferenceEquals(left, right))
        {
            return true;
        }

        if (left is null || right is null || left.Count != right.Count)
        {
            return false;
        }

        foreach (var pair in left)
        {
            if (!right.TryGetValue(pair.Key, out var value) ||
                !string.Equals(pair.Value, value, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }
}
