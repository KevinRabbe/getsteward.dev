using System.Security.Cryptography;
using System.Text.Json;
using SharedWorlds.Core.Abstractions;
using SharedWorlds.Core.Environment;
using SharedWorlds.Core.Errors;

namespace SharedWorlds.GameAdapters.Factorio;

internal static partial class FactorioWorldOperations
{
    public static async Task<PreparedWorld> PrepareEnvironmentAsync(
        GameInstallation installation,
        EnvironmentManifest requiredEnvironment,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!string.Equals(requiredEnvironment.AdapterId, "factorio", StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"Environment belongs to adapter '{requiredEnvironment.AdapterId}', not Factorio.",
                nameof(requiredEnvironment));
        }

        FactorioModInputSafety.RequireReproductionInputs(installation);

        var workspace = Path.Combine(
            GetLocalWorkRoot(),
            Guid.NewGuid().ToString("N"));
        var workspaceConfigDirectory = Path.Combine(workspace, WorkspaceConfigDirectoryName);
        var workspaceUserDataDirectory = Path.Combine(workspace, WorkspaceUserDataDirectoryName);
        var workspaceSavesDirectory = Path.Combine(workspaceUserDataDirectory, SavesDirectoryName);
        var workspaceModsDirectory = Path.Combine(workspace, ModsDirectoryName);

        Directory.CreateDirectory(workspaceConfigDirectory);
        Directory.CreateDirectory(workspaceSavesDirectory);
        Directory.CreateDirectory(workspaceModsDirectory);

        try
        {
            await CreateWorkspaceConfigAsync(
                installation,
                Path.Combine(workspaceConfigDirectory, WorkspaceConfigFileName),
                workspaceUserDataDirectory,
                cancellationToken);

            await PrepareWorkspaceModsAsync(
                installation,
                requiredEnvironment,
                workspaceModsDirectory,
                cancellationToken);

            return new PreparedWorld(installation, workspace, requiredEnvironment);
        }
        catch
        {
            TryDeleteDirectory(workspace);
            throw;
        }
    }

    private static async Task PrepareWorkspaceModsAsync(
        GameInstallation installation,
        EnvironmentManifest requiredEnvironment,
        string workspaceModsDirectory,
        CancellationToken cancellationToken)
    {
        var sourceUserDataPath = GetRequiredMetadata(
            installation,
            FactorioInstallationDiscovery.UserDataPathKey);
        var sourceModsDirectory = Path.Combine(sourceUserDataPath, ModsDirectoryName);
        var availableArtifacts = FactorioModCatalog.Discover(sourceModsDirectory);
        var requiredMods = requiredEnvironment.Components
            .Where(component => string.Equals(component.Kind, "mod", StringComparison.OrdinalIgnoreCase))
            .OrderBy(component => component.Id, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var duplicateMod = requiredMods
            .GroupBy(component => component.Id, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicateMod is not null)
        {
            throw new EnvironmentReproductionException(
                "factorio",
                $"environment manifest contains duplicate mod '{duplicateMod.Key}'.");
        }

        foreach (var component in requiredMods)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (string.Equals(component.Source, "builtin", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!string.Equals(component.Source, "user", StringComparison.OrdinalIgnoreCase))
            {
                throw new EnvironmentReproductionException(
                    "factorio",
                    $"mod '{component.Id}' has unsupported source '{component.Source ?? "unknown"}'.");
            }

            if (string.IsNullOrWhiteSpace(component.Version))
            {
                throw new EnvironmentReproductionException(
                    "factorio",
                    $"mod '{component.Id}' has no exact recorded version.");
            }

            var artifact = FactorioModCatalog.FindExact(
                availableArtifacts,
                component.Id,
                component.Version);
            if (artifact is null)
            {
                throw new EnvironmentReproductionException(
                    "factorio",
                    $"required mod '{component.Id}' version '{component.Version}' is not available in '{sourceModsDirectory}'.");
            }

            var destination = Path.Combine(
                workspaceModsDirectory,
                Path.GetFileName(artifact.Path));
            if (artifact.IsDirectory)
            {
                await CopyDirectoryAsync(artifact.Path, destination, cancellationToken);
            }
            else
            {
                await CopyFileAsync(artifact.Path, destination, overwrite: false, cancellationToken);
            }
        }

        await PrepareModSettingsAsync(
            sourceModsDirectory,
            workspaceModsDirectory,
            requiredEnvironment,
            cancellationToken);
        await WriteModListAsync(workspaceModsDirectory, requiredMods, cancellationToken);
    }

    private static async Task PrepareModSettingsAsync(
        string sourceModsDirectory,
        string workspaceModsDirectory,
        EnvironmentManifest requiredEnvironment,
        CancellationToken cancellationToken)
    {
        var sourcePath = Path.Combine(sourceModsDirectory, ModSettingsFileName);
        var destinationPath = Path.Combine(workspaceModsDirectory, ModSettingsFileName);

        if (requiredEnvironment.Configuration.TryGetValue(
                FactorioEnvironmentInspector.ModSettingsHashConfigurationKey,
                out var expectedHash))
        {
            if (!File.Exists(sourcePath))
            {
                throw new EnvironmentReproductionException(
                    "factorio",
                    "the required mod startup-settings file is missing from the current Factorio mod directory.");
            }

            var actualHash = await ComputeSha256Async(sourcePath, cancellationToken);
            if (!string.Equals(expectedHash, actualHash, StringComparison.OrdinalIgnoreCase))
            {
                throw new EnvironmentReproductionException(
                    "factorio",
                    "the current mod startup settings do not match the settings fingerprint recorded for this World.");
            }

            await CopyFileAsync(sourcePath, destinationPath, overwrite: false, cancellationToken);
            return;
        }

        // Legacy EnvironmentRevisions created before startup-settings fingerprinting had no durable
        // value to verify. Preserve their previous behavior by copying current settings when present,
        // but new revisions record a hash and therefore fail instead of silently accepting drift.
        if (File.Exists(sourcePath))
        {
            await CopyFileAsync(sourcePath, destinationPath, overwrite: false, cancellationToken);
        }
    }

    private static async Task WriteModListAsync(
        string workspaceModsDirectory,
        IReadOnlyList<EnvironmentComponent> requiredMods,
        CancellationToken cancellationToken)
    {
        var document = new
        {
            mods = requiredMods.Select(component => new
            {
                name = component.Id,
                enabled = true
            }).ToArray()
        };
        var json = JsonSerializer.Serialize(
            document,
            new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(
            Path.Combine(workspaceModsDirectory, ModListFileName),
            json,
            cancellationToken);
    }

    private static async Task<string> ComputeSha256Async(
        string path,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 64 * 1024,
            useAsync: true);
        using var sha256 = SHA256.Create();
        return Convert.ToHexString(await sha256.ComputeHashAsync(stream, cancellationToken));
    }

    private static async Task CreateWorkspaceConfigAsync(
        GameInstallation installation,
        string destinationConfigPath,
        string workspaceUserDataDirectory,
        CancellationToken cancellationToken)
    {
        var sourceUserDataPath = GetRequiredMetadata(
            installation,
            FactorioInstallationDiscovery.UserDataPathKey);
        var sourceConfigPath = Path.Combine(
            sourceUserDataPath,
            WorkspaceConfigDirectoryName,
            WorkspaceConfigFileName);

        string[] lines;
        if (File.Exists(sourceConfigPath))
        {
            lines = await File.ReadAllLinesAsync(sourceConfigPath, cancellationToken);
        }
        else
        {
            lines =
            [
                "[path]",
                "read-data=__PATH__system-read-data__",
                $"write-data={Path.GetFullPath(workspaceUserDataDirectory)}"
            ];
        }

        var rewritten = RewriteWriteDataPath(lines, Path.GetFullPath(workspaceUserDataDirectory));
        await File.WriteAllLinesAsync(destinationConfigPath, rewritten, cancellationToken);
    }

    private static IReadOnlyList<string> RewriteWriteDataPath(
        IReadOnlyList<string> lines,
        string workspaceUserDataDirectory)
    {
        var result = new List<string>(lines.Count + 2);
        var inPathSection = false;
        var pathSectionFound = false;
        var writeDataReplaced = false;

        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("[", StringComparison.Ordinal) &&
                trimmed.EndsWith("]", StringComparison.Ordinal))
            {
                if (inPathSection && !writeDataReplaced)
                {
                    result.Add($"write-data={workspaceUserDataDirectory}");
                    writeDataReplaced = true;
                }

                inPathSection = string.Equals(trimmed, "[path]", StringComparison.OrdinalIgnoreCase);
                pathSectionFound |= inPathSection;
                result.Add(line);
                continue;
            }

            if (inPathSection && trimmed.StartsWith("write-data=", StringComparison.OrdinalIgnoreCase))
            {
                result.Add($"write-data={workspaceUserDataDirectory}");
                writeDataReplaced = true;
                continue;
            }

            result.Add(line);
        }

        if (inPathSection && !writeDataReplaced)
        {
            result.Add($"write-data={workspaceUserDataDirectory}");
        }

        if (!pathSectionFound)
        {
            result.Add(string.Empty);
            result.Add("[path]");
            result.Add("read-data=__PATH__system-read-data__");
            result.Add($"write-data={workspaceUserDataDirectory}");
        }

        return result;
    }
}
