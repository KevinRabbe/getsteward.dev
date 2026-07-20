using System.IO.Compression;
using System.Text.Json;

namespace SharedWorlds.GameAdapters.Factorio;

internal static class FactorioModCatalog
{
    public static IReadOnlyList<FactorioModArtifact> Discover(string modsDirectory)
    {
        if (!Directory.Exists(modsDirectory))
        {
            return [];
        }

        var artifacts = new List<FactorioModArtifact>();

        foreach (var directory in Directory.EnumerateDirectories(modsDirectory, "*", SearchOption.TopDirectoryOnly))
        {
            var artifact = TryReadDirectory(directory);
            if (artifact is not null)
            {
                artifacts.Add(artifact);
            }
        }

        foreach (var archivePath in Directory.EnumerateFiles(modsDirectory, "*.zip", SearchOption.TopDirectoryOnly))
        {
            var artifact = TryReadArchive(archivePath);
            if (artifact is not null)
            {
                artifacts.Add(artifact);
            }
        }

        return artifacts
            .OrderBy(artifact => artifact.Name, StringComparer.OrdinalIgnoreCase)
            .ThenByDescending(artifact => artifact.Version, StringComparer.OrdinalIgnoreCase)
            .ThenBy(artifact => artifact.Path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public static FactorioModArtifact? FindExact(
        IReadOnlyList<FactorioModArtifact> artifacts,
        string modName,
        string version)
        => artifacts.FirstOrDefault(artifact =>
            string.Equals(artifact.Name, modName, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(artifact.Version, version, StringComparison.OrdinalIgnoreCase));

    private static FactorioModArtifact? TryReadDirectory(string directory)
    {
        var infoPath = Path.Combine(directory, "info.json");
        if (!File.Exists(infoPath))
        {
            return null;
        }

        try
        {
            using var stream = File.OpenRead(infoPath);
            var identity = ReadIdentity(stream);
            return identity is null
                ? null
                : new FactorioModArtifact(identity.Value.Name, identity.Value.Version, directory, IsDirectory: true);
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static FactorioModArtifact? TryReadArchive(string archivePath)
    {
        try
        {
            using var archive = ZipFile.OpenRead(archivePath);
            var infoEntry = archive.Entries.FirstOrDefault(entry =>
                entry.FullName.Equals("info.json", StringComparison.OrdinalIgnoreCase) ||
                entry.FullName.EndsWith("/info.json", StringComparison.OrdinalIgnoreCase));
            if (infoEntry is null)
            {
                return null;
            }

            using var stream = infoEntry.Open();
            var identity = ReadIdentity(stream);
            return identity is null
                ? null
                : new FactorioModArtifact(identity.Value.Name, identity.Value.Version, archivePath, IsDirectory: false);
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
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

    private static (string Name, string Version)? ReadIdentity(Stream stream)
    {
        using var document = JsonDocument.Parse(stream);
        if (!document.RootElement.TryGetProperty("name", out var nameElement) ||
            !document.RootElement.TryGetProperty("version", out var versionElement))
        {
            return null;
        }

        var name = nameElement.GetString();
        var version = versionElement.GetString();
        return string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(version)
            ? null
            : (name, version);
    }
}

internal sealed record FactorioModArtifact(
    string Name,
    string Version,
    string Path,
    bool IsDirectory);
