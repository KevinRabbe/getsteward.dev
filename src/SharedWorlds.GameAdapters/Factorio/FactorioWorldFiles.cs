using SharedWorlds.Core.Abstractions;

namespace SharedWorlds.GameAdapters.Factorio;

internal static partial class FactorioWorldOperations
{
    private const string PreparedSaveFileName = "world.zip";
    private const string WorkspaceConfigDirectoryName = "config";
    private const string WorkspaceConfigFileName = "config.ini";
    private const string WorkspaceUserDataDirectoryName = "user-data";
    private const string SavesDirectoryName = "saves";
    private const string ModsDirectoryName = "mods";
    private const string ModListFileName = "mod-list.json";
    private const string ModSettingsFileName = "mod-settings.dat";

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

        return Path.Combine(basePath, "SafeWorld", "factorio");
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
