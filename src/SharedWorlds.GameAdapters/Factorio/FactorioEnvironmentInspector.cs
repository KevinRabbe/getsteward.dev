using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using SharedWorlds.Core.Abstractions;
using SharedWorlds.Core.Environment;

namespace SharedWorlds.GameAdapters.Factorio;

internal static partial class FactorioEnvironmentInspector
{
    internal const string ModSettingsHashConfigurationKey = "factorio.mod-settings.sha256";
    internal const long MaximumModListBytes = 4L * 1024 * 1024;

    public static async Task<EnvironmentManifest> InspectAsync(
        GameInstallation installation,
        CancellationToken cancellationToken)
    {
        var userDataPath = GetRequiredMetadata(installation, FactorioInstallationDiscovery.UserDataPathKey);
        FactorioModInputSafety.RequireInspectionInputs(installation);

        var gameVersion = await ReadInstalledGameVersionAsync(installation, cancellationToken);
        var components = ReadEnabledMods(installation.RootPath, userDataPath, gameVersion);
        var configuration = ReadConfiguration(userDataPath);

        return new EnvironmentManifest(
            SchemaVersion: 1,
            AdapterId: "factorio",
            GameVersion: gameVersion,
            Components: components,
            Configuration: configuration);
    }

    internal static async Task<string> ReadInstalledGameVersionAsync(
        GameInstallation installation,
        CancellationToken cancellationToken)
    {
        var baseInfoPath = Path.Combine(installation.RootPath, "data", "base", "info.json");
        var baseVersion = ReadVersionFromJsonFile(baseInfoPath);
        if (!string.IsNullOrWhiteSpace(baseVersion))
        {
            return baseVersion;
        }

        var executablePath = GetRequiredMetadata(installation, FactorioInstallationDiscovery.ExecutablePathKey);
        return await ReadExecutableVersionAsync(executablePath, cancellationToken);
    }

    private static async Task<string> ReadExecutableVersionAsync(
        string executablePath,
        CancellationToken cancellationToken)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = executablePath,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            }
        };
        process.StartInfo.ArgumentList.Add("--version");

        if (!process.Start())
        {
            throw new InvalidOperationException($"Failed to start Factorio executable '{executablePath}'.");
        }

        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync(cancellationToken);
        var output = await outputTask;
        var error = await errorTask;

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"Factorio --version exited with code {process.ExitCode}: {error.Trim()}");
        }

        var match = VersionRegex().Match(output);
        if (!match.Success)
        {
            throw new InvalidOperationException("Could not parse Factorio version output.");
        }

        return match.Groups[1].Value;
    }

    private static IReadOnlyList<EnvironmentComponent> ReadEnabledMods(
        string installationRoot,
        string userDataPath,
        string gameVersion)
    {
        var modsDirectory = Path.Combine(userDataPath, "mods");
        var modListPath = Path.Combine(modsDirectory, "mod-list.json");
        if (!File.Exists(modListPath))
        {
            return [];
        }

        using var stream = File.OpenRead(modListPath);
        if (stream.Length > MaximumModListBytes)
        {
            throw new InvalidOperationException(
                $"Factorio mod-list file '{modListPath}' exceeds Steward's {MaximumModListBytes}-byte environment metadata safety limit.");
        }

        using var document = JsonDocument.Parse(stream);
        if (!document.RootElement.TryGetProperty("mods", out var mods) ||
            mods.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var userModArtifacts = FactorioModCatalog.Discover(modsDirectory);
        var result = new List<EnvironmentComponent>();
        foreach (var mod in mods.EnumerateArray())
        {
            if (!mod.TryGetProperty("name", out var nameElement))
            {
                continue;
            }

            var name = nameElement.GetString();
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            var enabled = !mod.TryGetProperty("enabled", out var enabledElement) ||
                          enabledElement.ValueKind != JsonValueKind.False;
            if (!enabled)
            {
                continue;
            }

            var (version, source) = ResolveModVersion(
                installationRoot,
                userModArtifacts,
                name,
                gameVersion);
            result.Add(new EnvironmentComponent(
                Kind: "mod",
                Id: name,
                Version: version,
                Source: source));
        }

        return result;
    }

    private static IReadOnlyDictionary<string, string> ReadConfiguration(string userDataPath)
    {
        var configuration = new Dictionary<string, string>(StringComparer.Ordinal);
        var modSettingsPath = Path.Combine(userDataPath, "mods", "mod-settings.dat");
        if (!File.Exists(modSettingsPath))
        {
            return configuration;
        }

        using var stream = File.OpenRead(modSettingsPath);
        configuration[ModSettingsHashConfigurationKey] = Convert.ToHexString(SHA256.HashData(stream));
        return configuration;
    }

    private static (string? Version, string Source) ResolveModVersion(
        string installationRoot,
        IReadOnlyList<FactorioModArtifact> userModArtifacts,
        string modName,
        string gameVersion)
    {
        var builtInInfo = Path.Combine(installationRoot, "data", modName, "info.json");
        if (File.Exists(builtInInfo))
        {
            return (ReadVersionFromJsonFile(builtInInfo) ?? gameVersion, "builtin");
        }

        var artifact = userModArtifacts.FirstOrDefault(candidate =>
            string.Equals(candidate.Name, modName, StringComparison.OrdinalIgnoreCase));
        return artifact is null
            ? (null, "user")
            : (artifact.Version, "user");
    }

    private static string? ReadVersionFromJsonFile(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            using var stream = File.OpenRead(path);
            using var document = JsonDocument.Parse(stream);
            return document.RootElement.TryGetProperty("version", out var version)
                ? version.GetString()
                : null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string GetRequiredMetadata(GameInstallation installation, string key)
    {
        if (installation.Metadata is null ||
            !installation.Metadata.TryGetValue(key, out var value) ||
            string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException(
                $"Factorio installation '{installation.RootPath}' is missing required metadata '{key}'.");
        }

        return value;
    }

    [GeneratedRegex(@"(?im)(?:Version:\s*)?(\d+\.\d+(?:\.\d+)?)")]
    private static partial Regex VersionRegex();
}
