namespace SharedWorlds.GameAdapters.ProjectZomboid;

internal sealed class ProjectZomboidRuntimeManagementConfiguration
{
    public ProjectZomboidRuntimeManagementConfiguration(
        string configurationPath,
        int port,
        string password)
    {
        ConfigurationPath = configurationPath;
        Port = port;
        Password = password;
    }

    public string ConfigurationPath { get; }

    public int Port { get; }

    public string Password { get; }

    public override string ToString()
        => $"Project Zomboid transient management endpoint on loopback port {Port}.";
}

internal static class ProjectZomboidRuntimeManagementConfigurationWriter
{
    internal static Task<ProjectZomboidRuntimeManagementConfiguration> MaterializeAsync(
        ProjectZomboidDedicatedServerHostInputs inputs,
        CancellationToken cancellationToken)
        => MaterializeAsync(
            inputs,
            ProjectZomboidTransientManagementConfigurationBuilder.CreateTransientPassword(),
            cancellationToken);

    internal static async Task<ProjectZomboidRuntimeManagementConfiguration> MaterializeAsync(
        ProjectZomboidDedicatedServerHostInputs inputs,
        string transientPassword,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        cancellationToken.ThrowIfCancellationRequested();

        var configurationPath = GetConfigurationPath(inputs);
        ProjectZomboidWorkspaceOwnership.RequireOwnedPath(
            inputs.CacheDirectory,
            configurationPath,
            "runtime server configuration");
        PreflightConfigurationFile(configurationPath);

        var sourceBytes = await File.ReadAllBytesAsync(configurationPath, cancellationToken);
        var runtime = ProjectZomboidTransientManagementConfigurationBuilder.Create(
            sourceBytes,
            transientPassword);

        await PublishAtomicallyAsync(
            inputs.CacheDirectory,
            configurationPath,
            runtime.RuntimeBytes,
            cancellationToken);

        return new ProjectZomboidRuntimeManagementConfiguration(
            configurationPath,
            runtime.Port,
            transientPassword);
    }

    internal static async Task ScrubAsync(
        ProjectZomboidDedicatedServerHostInputs inputs,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        cancellationToken.ThrowIfCancellationRequested();

        var configurationPath = GetConfigurationPath(inputs);
        ProjectZomboidWorkspaceOwnership.RequireOwnedPath(
            inputs.CacheDirectory,
            configurationPath,
            "runtime server configuration");
        PreflightConfigurationFile(configurationPath);

        var currentBytes = await File.ReadAllBytesAsync(configurationPath, cancellationToken);
        var scrubbedBytes = ProjectZomboidPortableServerConfiguration.Sanitize(currentBytes);
        if (currentBytes.AsSpan().SequenceEqual(scrubbedBytes))
        {
            return;
        }

        await PublishAtomicallyAsync(
            inputs.CacheDirectory,
            configurationPath,
            scrubbedBytes,
            cancellationToken);
    }

    private static string GetConfigurationPath(ProjectZomboidDedicatedServerHostInputs inputs)
        => Path.GetFullPath(Path.Combine(
            inputs.CacheDirectory,
            "Server",
            inputs.ServerName + ".ini"));

    private static void PreflightConfigurationFile(string path)
    {
        var file = new FileInfo(path);
        if (!file.Exists)
        {
            throw new FileNotFoundException(
                "Project Zomboid runtime server configuration does not exist.",
                path);
        }

        if (file.Length > ProjectZomboidTransientManagementConfigurationBuilder.MaximumConfigurationBytes)
        {
            throw new InvalidDataException(
                $"Project Zomboid server configuration exceeds Steward's {ProjectZomboidTransientManagementConfigurationBuilder.MaximumConfigurationBytes}-byte management safety limit.");
        }
    }

    private static async Task PublishAtomicallyAsync(
        string workingDirectory,
        string destinationPath,
        byte[] runtimeBytes,
        CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(destinationPath)
            ?? throw new InvalidOperationException(
                $"Could not determine the Project Zomboid server configuration directory for '{destinationPath}'.");
        var temporaryPath = Path.Combine(
            directory,
            $".{Path.GetFileName(destinationPath)}.sharedworlds-{Guid.NewGuid():N}.tmp");

        ProjectZomboidWorkspaceOwnership.RequireOwnedPath(
            workingDirectory,
            temporaryPath,
            "temporary runtime server configuration");

        try
        {
            await using (var stream = new FileStream(
                             temporaryPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             bufferSize: 64 * 1024,
                             useAsync: true))
            {
                await stream.WriteAsync(runtimeBytes, cancellationToken);
                await stream.FlushAsync(cancellationToken);
                stream.Flush(flushToDisk: true);
            }

            cancellationToken.ThrowIfCancellationRequested();
            ProjectZomboidWorkspaceOwnership.RequireOwnedPath(
                workingDirectory,
                destinationPath,
                "runtime server configuration");
            File.Move(temporaryPath, destinationPath, overwrite: true);
        }
        finally
        {
            TryDeleteFile(temporaryPath);
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
