using SharedWorlds.Core.Abstractions;

namespace SharedWorlds.GameAdapters.Palworld;

internal static class PalworldDedicatedRuntimeInputSafety
{
    internal const long MaximumDedicatedServerManifestBytes = 4L * 1024 * 1024;
    internal const long MaximumManagedConfigurationBytes = 4L * 1024 * 1024;
    private static readonly string[] ConfigDirectories = ["Pal", "Saved", "Config", "WindowsServer"];

    public static void ValidateEnvironmentInspection(GameInstallation installation)
    {
        ArgumentNullException.ThrowIfNull(installation);
        if (installation.Metadata is null ||
            !installation.Metadata.TryGetValue(
                PalworldInstallationDiscovery.DedicatedServerManifestPathKey,
                out var manifestPath) ||
            string.IsNullOrWhiteSpace(manifestPath))
        {
            return;
        }

        // Missing manifest remains the existing truthful "unknown build" state. A linked or oversized
        // manifest is different: its bytes exist, but Steward must not promote unsafe/unbounded bytes
        // to an exact Palworld build identity.
        var fullManifestPath = Path.GetFullPath(manifestPath);
        if (!TryRequireRegularFile(
                fullManifestPath,
                "Palworld dedicated-server Steam manifest"))
        {
            return;
        }

        RequireFileSizeAtMost(
            fullManifestPath,
            MaximumDedicatedServerManifestBytes,
            "Palworld dedicated-server Steam manifest");
    }

    public static void ValidatePreparationInputs(GameInstallation installation)
    {
        ArgumentNullException.ThrowIfNull(installation);
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var serverRoot = GetRequiredMetadata(
            installation,
            PalworldInstallationDiscovery.DedicatedServerRootPathKey);
        var serverExecutable = GetRequiredMetadata(
            installation,
            PalworldInstallationDiscovery.DedicatedServerExecutablePathKey);
        RequireRegularDirectory(serverRoot, "Palworld dedicated-server root");
        RequireRegularFile(serverExecutable, "Palworld dedicated-server executable");

        _ = EnsureRegularDirectoryChain(
            serverRoot,
            "Pal",
            "Saved",
            "SaveGames",
            "0");
    }

    public static void ValidateManagedHostInputs(PreparedWorld world)
    {
        ArgumentNullException.ThrowIfNull(world);
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var installation = world.Installation;
        var serverRoot = GetRequiredMetadata(
            installation,
            PalworldInstallationDiscovery.DedicatedServerRootPathKey);
        var serverExecutable = GetRequiredMetadata(
            installation,
            PalworldInstallationDiscovery.DedicatedServerExecutablePathKey);
        RequireRegularDirectory(serverRoot, "Palworld dedicated-server root");
        RequireRegularFile(serverExecutable, "Palworld dedicated-server executable");

        var worldPath = Path.GetFullPath(world.WorkingDirectory);
        RequireRegularDirectory(worldPath, "Palworld prepared World root");
        RequireRegularFile(
            Path.Combine(worldPath, "Level.sav"),
            "Palworld prepared World Level.sav");
        RequireRegularFile(
            Path.Combine(worldPath, "WorldOption.sav"),
            "Palworld canonical WorldOption.sav");

        if (!TryGetRegularFileUnderRoot(
                serverRoot,
                ConfigDirectories,
                "GameUserSettings.ini",
                "Palworld GameUserSettings.ini",
                out var gameUserSettingsPath))
        {
            throw new InvalidOperationException(
                "PalServer has not initialized GameUserSettings.ini yet. Start the dedicated server once and stop it before Steward hosts this World.");
        }

        RequireFileSizeAtMost(
            gameUserSettingsPath,
            MaximumManagedConfigurationBytes,
            "Palworld GameUserSettings.ini");

        if (!TryGetRegularFileUnderRoot(
                serverRoot,
                ConfigDirectories,
                "PalWorldSettings.ini",
                "Palworld PalWorldSettings.ini",
                out var palWorldSettingsPath))
        {
            throw new InvalidOperationException(
                "PalWorldSettings.ini does not exist. Start and stop the dedicated server once before Steward manages this World.");
        }

        RequireFileSizeAtMost(
            palWorldSettingsPath,
            MaximumManagedConfigurationBytes,
            "Palworld PalWorldSettings.ini");
    }

    public static void RequireRegularDirectory(string path, string description)
    {
        if (!TryRequireRegularDirectory(path, description))
        {
            throw new InvalidOperationException($"{description} was not found: {path}");
        }
    }

    public static void RequireRegularFile(string path, string description)
    {
        if (!TryRequireRegularFile(path, description))
        {
            throw new InvalidOperationException($"{description} was not found: {path}");
        }
    }

    public static bool TryRequireRegularFile(string path, string description)
        => TryRequireRegularPath(path, description, expectDirectory: false);

    public static bool TryGetRegularFileUnderRoot(
        string root,
        IReadOnlyList<string> relativeDirectories,
        string fileName,
        string description,
        out string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        ArgumentNullException.ThrowIfNull(relativeDirectories);
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);

        var current = Path.GetFullPath(root);
        RequireRegularDirectory(current, "Palworld dedicated-server root");
        foreach (var segment in relativeDirectories)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(segment);
            current = Path.Combine(current, segment);
            if (!TryRequireRegularDirectory(current, $"Palworld dedicated-server '{segment}' directory"))
            {
                path = Path.Combine(current, fileName);
                return false;
            }
        }

        path = Path.Combine(current, fileName);
        return TryRequireRegularFile(path, description);
    }

    public static string EnsureRegularDirectoryChain(
        string root,
        params string[] relativeDirectories)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        ArgumentNullException.ThrowIfNull(relativeDirectories);

        var current = Path.GetFullPath(root);
        RequireRegularDirectory(current, "Palworld dedicated-server root");
        foreach (var segment in relativeDirectories)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(segment);
            current = Path.Combine(current, segment);
            if (!TryRequireRegularDirectory(current, $"Palworld dedicated-server '{segment}' directory"))
            {
                Directory.CreateDirectory(current);
                RequireRegularDirectory(current, $"Palworld dedicated-server '{segment}' directory");
            }
        }

        return current;
    }

    private static void RequireFileSizeAtMost(
        string path,
        long maximumBytes,
        string description)
    {
        long length;
        try
        {
            length = new FileInfo(path).Length;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException(
                $"{description} size could not be inspected safely: {path}: {exception.Message}",
                exception);
        }

        if (length > maximumBytes)
        {
            throw new InvalidOperationException(
                $"{description} exceeds Steward's {maximumBytes}-byte input safety limit: {path}");
        }
    }

    private static bool TryRequireRegularDirectory(string path, string description)
        => TryRequireRegularPath(path, description, expectDirectory: true);

    private static bool TryRequireRegularPath(
        string path,
        string description,
        bool expectDirectory)
    {
        FileAttributes attributes;
        try
        {
            attributes = File.GetAttributes(path);
        }
        catch (Exception exception) when (
            exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return false;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException(
                $"{description} could not be inspected safely: {path}: {exception.Message}",
                exception);
        }

        ValidateRegularAttributes(path, description, expectDirectory, attributes);
        return true;
    }

    private static void ValidateRegularAttributes(
        string path,
        string description,
        bool expectDirectory,
        FileAttributes attributes)
    {
        if ((attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidOperationException(
                $"{description} is linked or a reparse point. Steward will not use bytes outside the Palworld dedicated runtime: {path}");
        }

        var isDirectory = (attributes & FileAttributes.Directory) != 0;
        if (isDirectory != expectDirectory)
        {
            throw new InvalidOperationException(
                $"{description} is not a {(expectDirectory ? "directory" : "regular file")}: {path}");
        }
    }

    private static string GetRequiredMetadata(GameInstallation installation, string key)
    {
        if (installation.Metadata is null ||
            !installation.Metadata.TryGetValue(key, out var value) ||
            string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException(
                $"Palworld installation metadata is missing required value '{key}'.");
        }

        return Path.GetFullPath(value);
    }
}
