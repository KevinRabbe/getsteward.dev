using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using SharedWorlds.Core.Abstractions;
using SharedWorlds.Core.Environment;
using SharedWorlds.Core.Errors;

namespace SharedWorlds.GameAdapters.Factorio;

internal static class FactorioWorldOperations
{
    private const string PreparedSaveFileName = "world.zip";
    private const string WorkspaceConfigDirectoryName = "config";
    private const string WorkspaceConfigFileName = "config.ini";
    private const string WorkspaceUserDataDirectoryName = "user-data";
    private const string SavesDirectoryName = "saves";
    private const string ModsDirectoryName = "mods";
    private const string ModListFileName = "mod-list.json";
    private const string ModSettingsFileName = "mod-settings.dat";

    public static IReadOnlyList<DetectedWorld> DiscoverWorlds(GameInstallation installation)
    {
        var userDataPath = GetRequiredMetadata(installation, FactorioInstallationDiscovery.UserDataPathKey);
        var savesPath = Path.Combine(userDataPath, SavesDirectoryName);

        if (!Directory.Exists(savesPath))
        {
            return [];
        }

        return Directory
            .EnumerateFiles(savesPath, "*.zip", SearchOption.TopDirectoryOnly)
            .Where(path => !IsAutosave(path))
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .Select(path => new DetectedWorld(
                Id: Path.GetFullPath(path),
                DisplayName: Path.GetFileNameWithoutExtension(path),
                SourcePath: Path.GetFullPath(path)))
            .ToArray();
    }

    public static async Task<CapturedState> CaptureDetectedWorldAsync(
        DetectedWorld world,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(world.SourcePath))
        {
            throw new FileNotFoundException("The detected Factorio save no longer exists.", world.SourcePath);
        }

        var package = CreatePackagePath();
        await CopyFileAsync(world.SourcePath, package, overwrite: false, cancellationToken);
        return new CapturedState(
            new StatePackage(Path.GetFileNameWithoutExtension(package), package),
            DateTimeOffset.UtcNow);
    }

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

    public static async Task RestoreStateAsync(
        PreparedWorld world,
        StatePackage state,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(state.Path))
        {
            throw new FileNotFoundException("The Factorio state package does not exist.", state.Path);
        }

        var destination = GetPreparedSavePath(world);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        await CopyFileAsync(state.Path, destination, overwrite: true, cancellationToken);
    }

    public static async Task<CapturedState> CaptureStateAsync(
        PreparedWorld world,
        CancellationToken cancellationToken)
    {
        var savesDirectory = GetWorkspaceSavesDirectory(world);
        var savePath = Directory.Exists(savesDirectory)
            ? Directory
                .EnumerateFiles(savesDirectory, "*.zip", SearchOption.TopDirectoryOnly)
                .Where(path => !IsAutosave(path))
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault()
            : null;

        if (savePath is null)
        {
            throw new FileNotFoundException(
                "The isolated Factorio workspace has no non-autosave save to capture.",
                savesDirectory);
        }

        var package = CreatePackagePath();
        await CopyFileAsync(savePath, package, overwrite: false, cancellationToken);
        return new CapturedState(
            new StatePackage(Path.GetFileNameWithoutExtension(package), package),
            DateTimeOffset.UtcNow);
    }

    public static Task<GameSessionHandle> LaunchLocalAsync(
        PreparedWorld world,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var savePath = GetPreparedSavePath(world);
        EnsurePreparedSaveExists(savePath, "local Factorio session");

        var process = StartFactorio(
            world,
            ["--load-game", savePath]);
        return Task.FromResult(new GameSessionHandle(process.Id, DateTimeOffset.UtcNow));
    }

    public static Task<GameSessionHandle> LaunchHostAsync(
        PreparedWorld world,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var savePath = GetPreparedSavePath(world);
        EnsurePreparedSaveExists(savePath, "Factorio host");

        var process = StartFactorio(
            world,
            ["--host", savePath]);
        return Task.FromResult(new GameSessionHandle(process.Id, DateTimeOffset.UtcNow));
    }

    public static Task<GameSessionHandle> LaunchClientAsync(
        PreparedWorld world,
        HostConnection host,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var address = host.Port is null
            ? host.Address
            : $"{host.Address}:{host.Port.Value}";

        var process = StartFactorio(
            world,
            ["--mp-connect", address]);
        return Task.FromResult(new GameSessionHandle(process.Id, DateTimeOffset.UtcNow));
    }

    internal static string GetExecutablePath(GameInstallation installation)
        => GetRequiredMetadata(installation, FactorioInstallationDiscovery.ExecutablePathKey);

    private static Process StartFactorio(
        PreparedWorld world,
        IReadOnlyList<string> operationArguments)
    {
        var executable = GetExecutablePath(world.Installation);
        var executableDirectory = Path.GetDirectoryName(executable)
            ?? throw new InvalidOperationException(
                $"Cannot determine Factorio executable directory for '{executable}'.");
        var configPath = GetWorkspaceConfigPath(world);
        var workspaceModDirectory = GetWorkspaceModsDirectory(world);

        if (!File.Exists(configPath))
        {
            throw new FileNotFoundException(
                "The isolated Factorio workspace config does not exist.",
                configPath);
        }

        if (!Directory.Exists(workspaceModDirectory))
        {
            throw new DirectoryNotFoundException(
                $"The isolated Factorio workspace mod directory does not exist: '{workspaceModDirectory}'.");
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            // Steam Factorio resolves steam_appid.txt relative to the process working directory.
            // Starting in the installation root can trigger SteamAPI_RestartAppIfNecessary,
            // causing the bootstrap PID to exit before the actual game session starts.
            WorkingDirectory = executableDirectory,
            UseShellExecute = false
        };

        // The workspace config redirects Factorio's write-data directory away from the user's
        // normal %APPDATA%/Factorio tree. This is what makes save writes session-local.
        startInfo.ArgumentList.Add("--config");
        startInfo.ArgumentList.Add(configPath);

        // Factorio receives only the adapter-owned workspace mod directory. Required user mods
        // are copied there at exact manifest versions during preparation; unrelated live mods are
        // deliberately excluded so a World cannot silently inherit later changes to the user's mod set.
        startInfo.ArgumentList.Add("--mod-directory");
        startInfo.ArgumentList.Add(workspaceModDirectory);

        foreach (var argument in operationArguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        return Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Failed to start Factorio executable '{executable}'.");
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

    private static void EnsurePreparedSaveExists(string savePath, string operation)
    {
        if (!File.Exists(savePath))
        {
            throw new FileNotFoundException(
                $"RestoreStateAsync must be called before launching a {operation}.",
                savePath);
        }
    }

    private static bool IsAutosave(string path)
        => Path.GetFileNameWithoutExtension(path)
            .StartsWith("_autosave", StringComparison.OrdinalIgnoreCase);

    private static string GetPreparedSavePath(PreparedWorld world)
        => Path.Combine(GetWorkspaceSavesDirectory(world), GetPreparedSaveFileName(world.DisplayName));

    private static string GetPreparedSaveFileName(string? displayName)
    {
        if (string.IsNullOrWhiteSpace(displayName))
        {
            return PreparedSaveFileName;
        }

        var sanitized = displayName.Trim();
        foreach (var invalidCharacter in Path.GetInvalidFileNameChars())
        {
            sanitized = sanitized.Replace(invalidCharacter, '_');
        }

        foreach (var invalidCharacter in "<>:\"/\\|?*")
        {
            sanitized = sanitized.Replace(invalidCharacter, '_');
        }

        sanitized = sanitized.TrimEnd('.', ' ');
        return string.IsNullOrWhiteSpace(sanitized)
            ? PreparedSaveFileName
            : $"{sanitized}.zip";
    }

    private static string GetWorkspaceSavesDirectory(PreparedWorld world)
        => Path.Combine(
            world.WorkingDirectory,
            WorkspaceUserDataDirectoryName,
            SavesDirectoryName);

    private static string GetWorkspaceConfigPath(PreparedWorld world)
        => Path.Combine(
            world.WorkingDirectory,
            WorkspaceConfigDirectoryName,
            WorkspaceConfigFileName);

    private static string GetWorkspaceModsDirectory(PreparedWorld world)
        => Path.Combine(world.WorkingDirectory, ModsDirectoryName);

    private static string CreatePackagePath()
    {
        var root = Path.Combine(GetLocalWorkRoot(), "packages");
        Directory.CreateDirectory(root);
        return Path.Combine(root, $"{Guid.NewGuid():N}.zip");
    }

    private static string GetLocalWorkRoot()
    {
        var basePath = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(basePath))
        {
            basePath = Path.GetTempPath();
        }

        return Path.Combine(basePath, "SharedWorlds", "factorio");
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

    private static async Task CopyDirectoryAsync(
        string source,
        string destination,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(destination);

        foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relative = Path.GetRelativePath(source, directory);
            Directory.CreateDirectory(Path.Combine(destination, relative));
        }

        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relative = Path.GetRelativePath(source, file);
            var destinationFile = Path.Combine(destination, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destinationFile)!);
            await CopyFileAsync(file, destinationFile, overwrite: false, cancellationToken);
        }
    }

    private static async Task CopyFileAsync(
        string source,
        string destination,
        bool overwrite,
        CancellationToken cancellationToken)
    {
        var destinationMode = overwrite ? FileMode.Create : FileMode.CreateNew;

        await using var sourceStream = new FileStream(
            source,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 1024 * 128,
            useAsync: true);

        await using var destinationStream = new FileStream(
            destination,
            destinationMode,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 1024 * 128,
            useAsync: true);

        await sourceStream.CopyToAsync(destinationStream, cancellationToken);
        await destinationStream.FlushAsync(cancellationToken);
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (IOException)
        {
            // Preparation already failed. Best-effort cleanup must not hide the original failure.
        }
        catch (UnauthorizedAccessException)
        {
            // Preparation already failed. Best-effort cleanup must not hide the original failure.
        }
    }
}
