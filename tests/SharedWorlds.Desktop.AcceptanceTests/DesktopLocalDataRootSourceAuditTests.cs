using Xunit;

namespace SharedWorlds.Desktop.AcceptanceTests;

public sealed class DesktopLocalDataRootSourceAuditTests
{
    private static readonly string[] ApplicationOwnedDirectoryNames =
    [
        "\"SharedWorlds\"",
        "\"Steward\"",
        "\"SafeWorld\""
    ];

    [Fact]
    public void DesktopSourcesHaveNoIndependentDurableLegacyLocalDataRoots()
    {
        var desktopRoot = FindRepositoryDirectory("src/SharedWorlds.Desktop");
        var violations = new List<string>();

        foreach (var path in Directory.EnumerateFiles(
                     desktopRoot,
                     "*.cs",
                     SearchOption.TopDirectoryOnly))
        {
            if (string.Equals(
                    Path.GetFileName(path),
                    "DesktopLocalDataRoot.cs",
                    StringComparison.Ordinal))
            {
                continue;
            }

            var source = File.ReadAllText(path);
            var relativePath = Path.GetRelativePath(desktopRoot, path);
            var legacyLiteralCount = source.Split(
                "\"SharedWorlds\"",
                StringSplitOptions.None).Length - 1;
            var isOneKnownEphemeralTransferBuffer =
                string.Equals(
                    relativePath,
                    "SteamPeerWorldRevisionExchange.cs",
                    StringComparison.Ordinal) &&
                legacyLiteralCount == 1 &&
                source.Contains("Path.GetTempPath()", StringComparison.Ordinal) &&
                source.Contains("\"peer-world-transfer\"", StringComparison.Ordinal);
            if (legacyLiteralCount > 0 && !isOneKnownEphemeralTransferBuffer)
            {
                violations.Add(
                    $"{relativePath}: unexpected legacy SharedWorlds path literal");
            }

            if (source.Contains(
                    "Environment.SpecialFolder.LocalApplicationData",
                    StringComparison.Ordinal))
            {
                violations.Add($"{relativePath}: independent LocalApplicationData resolution");
            }

            if (source.Contains("GetLocalDataRoot", StringComparison.Ordinal))
            {
                violations.Add($"{relativePath}: removed GetLocalDataRoot helper");
            }
        }

        Assert.True(
            violations.Count == 0,
            "Desktop durable local-data root violations:" + Environment.NewLine +
            string.Join(Environment.NewLine, violations));
    }

    [Fact]
    public void ProductSourcesDoNotRecreateApplicationOwnedLocalApplicationDataRoots()
    {
        var sourceRoot = FindRepositoryDirectory("src");
        var violations = new List<string>();

        foreach (var path in Directory.EnumerateFiles(
                     sourceRoot,
                     "*.cs",
                     SearchOption.AllDirectories))
        {
            var relativePath = Path.GetRelativePath(sourceRoot, path);
            if (string.Equals(
                    relativePath.Replace('\\', '/'),
                    "SharedWorlds.Desktop/DesktopLocalDataRoot.cs",
                    StringComparison.Ordinal))
            {
                // The migration/root authority must know the current and historical application
                // directory names. No other source may use a product-generation name to derive an
                // application-owned LocalApplicationData root. Game-native LocalApplicationData
                // discovery remains valid because it does not use one of these product names.
                continue;
            }

            var source = File.ReadAllText(path);
            if (!source.Contains(
                    "Environment.SpecialFolder.LocalApplicationData",
                    StringComparison.Ordinal))
            {
                continue;
            }

            foreach (var directoryName in ApplicationOwnedDirectoryNames)
            {
                if (!source.Contains(directoryName, StringComparison.Ordinal))
                {
                    continue;
                }

                violations.Add(
                    $"{relativePath}: reconstructs application-owned LocalApplicationData using {directoryName}");
            }
        }

        Assert.True(
            violations.Count == 0,
            "Product application-owned LocalApplicationData root violations:" + Environment.NewLine +
            string.Join(Environment.NewLine, violations));
    }

    private static string FindRepositoryDirectory(string relativePath)
    {
        var workspace = Environment.GetEnvironmentVariable("GITHUB_WORKSPACE");
        if (!string.IsNullOrWhiteSpace(workspace))
        {
            var candidate = Path.Combine(workspace, relativePath);
            if (Directory.Exists(candidate))
            {
                return candidate;
            }
        }

        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, relativePath);
            if (Directory.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new DirectoryNotFoundException(
            $"Could not locate repository directory '{relativePath}'.");
    }
}
