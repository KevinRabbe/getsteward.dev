using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json;
using System.Text.RegularExpressions;
using SharedWorlds.Core.Abstractions;
using SharedWorlds.Core.Environment;

namespace SharedWorlds.GameAdapters.Factorio;

internal static partial class FactorioEnvironmentInspector
{
    public static async Task<EnvironmentManifest> InspectAsync(
        GameInstallation installation,
        CancellationToken cancellationToken)
    {
        var executablePath = GetRequiredMetadata(installation, FactorioInstallationDiscovery.ExecutablePathKey);
        var userDataPath = GetRequiredMetadata(installation, FactorioInstallationDiscovery.UserDataPathKey);

        var gameVersion = await ReadGameVersionAsync(executablePath, cancellationToken);
        var components = ReadEnabledMods(installation.RootPath, userDataPath, gameVersion);

        return new EnvironmentManifest(
            SchemaVersion: 1,
            AdapterId: "factorio",
            GameVersion: gameVersion,
            Components: components,
            Configuration: new Dictionary<string, string>(StringComparer.Ordinal));
    }

    private static async Task<string> ReadGameVersionAsync(
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
        var modListPath = Path.Combine(userDataPath, "mods", "mod-list.json");
        if (!File.Exists(modListPath))
        {
            return [];
        }

        using var stream = File.OpenRead(modListPath);
        using var document = JsonDocument.Parse(stream);

        if (!document.RootElement.TryGetProperty("mods", out var mods) ||
            mods.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

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

            var (version, source) = ResolveModVersion(installationRoot, userDataPath, name, gameVersion);
            result.Add(new EnvironmentComponent(
                Kind: "mod",
                Id: name,
                Version: version,
                Source: source));
        }

        return result;
    }

    private static (string? Version, string Source) ResolveModVersion(
        string installationRoot,
        string userDataPath,
        string modName,
        string gameVersion)
    {
        var builtInInfo = Path.Combine(installationRoot, "data", modName, "info.json");
        if (File.Exists(builtInInfo))
        {
            return (ReadVersionFromJsonFile(builtInInfo) ?? gameVersion, "builtin");
        }

        var modsDirectory = Path.Combine(userDataPath, "mods");
        if (!Directory.Exists(modsDirectory))
        {
            return (null, "user");
        }

        var directoryCandidates = Directory
            .EnumerateDirectories(modsDirectory, $"{modName}_*")
            .OrderByDescending(Path.GetFileName, StringComparer.OrdinalIgnoreCase);

        foreach (var directory in directoryCandidates)
        {
            var infoPath = Path.Combine(directory, "info.json");
            var version = ReadVersionFromJsonFile(infoPath);
            if (version is not null)
            {
                return (version, "user");
            }
        }

        var zipCandidates = Directory
            .EnumerateFiles(modsDirectory, $"{modName}_*.zip")
            .OrderByDescending(Path.GetFileName, StringComparer.OrdinalIgnoreCase);

        foreach (var zipPath in zipCandidates)
        {
            var version = ReadVersionFromZip(zipPath);
            if (version is not null)
            {
                return (version, "user");
            }
        }

        return (null, "user");
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

    private static string? ReadVersionFromZip(string path)
    {
        try
        {
            using var archive = ZipFile.OpenRead(path);
            var infoEntry = archive.Entries.FirstOrDefault(entry =>
                entry.FullName.Equals("info.json", StringComparison.OrdinalIgnoreCase) ||
                entry.FullName.EndsWith("/info.json", StringComparison.OrdinalIgnoreCase));

            if (infoEntry is null)
            {
                return null;
            }

            using var stream = infoEntry.Open();
            using var document = JsonDocument.Parse(stream);
            return document.RootElement.TryGetProperty("version", out var version)
                ? version.GetString()
                : null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (InvalidDataException)
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
