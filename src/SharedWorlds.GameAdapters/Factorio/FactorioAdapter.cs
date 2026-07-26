using System.Collections.Concurrent;
using System.Diagnostics;
using SharedWorlds.Core.Abstractions;
using SharedWorlds.Core.Environment;
using SharedWorlds.Core.Errors;

namespace SharedWorlds.GameAdapters.Factorio;

public sealed partial class FactorioAdapter : IGameAdapter
{
    private const string ConfigDirectoryName = "config";
    private const string ConfigFileName = "config.ini";
    private const string PathSectionName = "path";

    private static readonly TimeSpan BootstrapExitThreshold = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan ReplacementProcessTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan ReplacementPollInterval = TimeSpan.FromMilliseconds(250);

    private readonly ConcurrentDictionary<int, FactorioLaunchObservation> _launchObservations = new();

    public string Id => "factorio";
    public string DisplayName => "Factorio";

    public GameAdapterCapabilities Capabilities =>
        GameAdapterCapabilities.Mods |
        GameAdapterCapabilities.AutomaticLocalLaunch |
        GameAdapterCapabilities.AutomaticHostLaunch |
        GameAdapterCapabilities.AutomaticClientJoin |
        GameAdapterCapabilities.ExactGameVersion |
        GameAdapterCapabilities.NativeWorldCreation;

    public Task<IReadOnlyList<GameInstallation>> DiscoverInstallationsAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(FactorioInstallationDiscovery.Discover());
    }

    public Task<IReadOnlyList<DetectedWorld>> DiscoverWorldsAsync(
        GameInstallation installation,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(FactorioWorldOperations.DiscoverWorlds(installation));
    }

    public Task<EnvironmentManifest> InspectEnvironmentAsync(
        GameInstallation installation,
        DetectedWorld world,
        CancellationToken cancellationToken = default)
        => FactorioEnvironmentInspector.InspectAsync(installation, cancellationToken);

    public Task<CapturedState> CaptureDetectedWorldAsync(
        GameInstallation installation,
        DetectedWorld world,
        CancellationToken cancellationToken = default)
        => FactorioWorldOperations.CaptureDetectedWorldAsync(world, cancellationToken);

    public async Task<PreparedWorld> PrepareEnvironmentAsync(
        GameInstallation installation,
        EnvironmentManifest requiredEnvironment,
        CancellationToken cancellationToken = default)
    {
        if (!string.Equals(requiredEnvironment.AdapterId, Id, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"Environment belongs to adapter '{requiredEnvironment.AdapterId}', not Factorio.",
                nameof(requiredEnvironment));
        }

        var installedVersion = await FactorioEnvironmentInspector.ReadInstalledGameVersionAsync(
            installation,
            cancellationToken);
        if (!string.Equals(installedVersion, requiredEnvironment.GameVersion, StringComparison.Ordinal))
        {
            throw new EnvironmentReproductionException(
                Id,
                $"installed game version '{installedVersion}' does not match required version '{requiredEnvironment.GameVersion}'.");
        }

        return await FactorioWorldOperations.PrepareEnvironmentAsync(
            installation,
            requiredEnvironment,
            cancellationToken);
    }

    public Task<CapturedState> CaptureStateAsync(
        PreparedWorld world,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(world);
        FactorioWorkspaceOwnership.RequireOwned(world.WorkingDirectory);
        return FactorioWorldOperations.CaptureStateAsync(world, cancellationToken);
    }

    public Task RestoreStateAsync(
        PreparedWorld world,
        StatePackage state,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(world);
        FactorioWorkspaceOwnership.RequireOwned(world.WorkingDirectory);
        return FactorioWorldOperations.RestoreStateAsync(world, state, cancellationToken);
    }

    public Task<GameSessionHandle> LaunchLocalAsync(
        PreparedWorld world,
        CancellationToken cancellationToken = default)
        => LaunchTrackedAsync(
            world,
            FactorioWorldOperations.LaunchLocalAsync,
            cancellationToken);

    public Task<GameSessionHandle> LaunchHostAsync(
        PreparedWorld world,
        CancellationToken cancellationToken = default)
        => LaunchTrackedAsync(
            world,
            FactorioWorldOperations.LaunchHostAsync,
            cancellationToken);

    public Task<GameSessionHandle> LaunchClientAsync(
        PreparedWorld world,
        HostConnection host,
        CancellationToken cancellationToken = default)
        => LaunchTrackedAsync(
            world,
            (preparedWorld, token) => FactorioWorldOperations.LaunchClientAsync(
                preparedWorld,
                host,
                token),
            cancellationToken);

    public async Task WaitForSessionEndAsync(
        GameSessionHandle session,
        CancellationToken cancellationToken = default)
    {
        _launchObservations.TryRemove(session.ProcessId, out var observation);

        var processId = session.ProcessId;
        var processStartedAt = session.StartedAt;
        var handoffCount = 0;
        var excludedProcessIds = observation is null
            ? new HashSet<int>()
            : new HashSet<int>(observation.BaselineProcessIds);

        while (true)
        {
            var lifetime = await WaitForProcessExitAsync(
                processId,
                processStartedAt,
                cancellationToken);
            // A normal Factorio session lives longer than the tiny Steam bootstrap process.
            // Only attempt handoff recovery for an almost-immediate exit from a tracked launch.
            if (observation is null || lifetime >= BootstrapExitThreshold)
            {
                return;
            }

            excludedProcessIds.Add(processId);
            var replacementProcessId = await FindReplacementProcessAsync(
                observation.ProcessName,
                excludedProcessIds,
                cancellationToken);
            if (replacementProcessId is null)
            {
                throw new InvalidOperationException(
                    "Factorio's launch process exited before a playable game session could be observed. " +
                    "The operation stopped instead of treating a launcher/bootstrap exit as a completed session.");
            }

            handoffCount++;
            if (handoffCount > 3)
            {
                throw new InvalidOperationException(
                    "Factorio performed too many rapid process handoffs to identify the playable session safely.");
            }

            processId = replacementProcessId.Value;
            processStartedAt = DateTimeOffset.UtcNow;
        }
    }

    public async Task FinalizePreparedWorldAsync(
        PreparedWorld world,
        PreparedWorldDisposition disposition,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(world);
        cancellationToken.ThrowIfCancellationRequested();
        FactorioWorkspaceOwnership.RequireOwned(world.WorkingDirectory);

        await TryPersistPlayerPreferencesAsync(world, cancellationToken);

        if (disposition == PreparedWorldDisposition.PreserveForRecovery)
        {
            return;
        }

        DeleteOwnedWorkspace(world.WorkingDirectory);
    }

    private async Task<GameSessionHandle> LaunchTrackedAsync(
        PreparedWorld world,
        Func<PreparedWorld, CancellationToken, Task<GameSessionHandle>> launch,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(world);
        FactorioWorkspaceOwnership.RequireOwned(world.WorkingDirectory);

        var executable = FactorioWorldOperations.GetExecutablePath(world.Installation);
        var processName = Path.GetFileNameWithoutExtension(executable);
        var baselineProcessIds = GetProcessIds(processName);

        var handle = await launch(world, cancellationToken);
        _launchObservations[handle.ProcessId] = new FactorioLaunchObservation(
            processName,
            baselineProcessIds);
        return handle;
    }

    private static async Task<TimeSpan> WaitForProcessExitAsync(
        int processId,
        DateTimeOffset startedAt,
        CancellationToken cancellationToken)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (ArgumentException)
        {
            // The process may have exited before observation started.
        }

        var lifetime = DateTimeOffset.UtcNow - startedAt;
        return lifetime < TimeSpan.Zero ? TimeSpan.Zero : lifetime;
    }

    private static async Task<int?> FindReplacementProcessAsync(
        string processName,
        HashSet<int> excludedProcessIds,
        CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + ReplacementProcessTimeout;

        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            foreach (var process in Process.GetProcessesByName(processName))
            {
                using (process)
                {
                    if (excludedProcessIds.Contains(process.Id))
                    {
                        continue;
                    }

                    try
                    {
                        if (!process.HasExited)
                        {
                            return process.Id;
                        }
                    }
                    catch (InvalidOperationException)
                    {
                        // The process disappeared while it was being inspected.
                    }
                }
            }

            await Task.Delay(ReplacementPollInterval, cancellationToken);
        }

        return null;
    }

    private static HashSet<int> GetProcessIds(string processName)
    {
        var processIds = new HashSet<int>();
        foreach (var process in Process.GetProcessesByName(processName))
        {
            using (process)
            {
                processIds.Add(process.Id);
            }
        }

        return processIds;
    }

    private static async Task TryPersistPlayerPreferencesAsync(
        PreparedWorld world,
        CancellationToken cancellationToken)
    {
        var workspaceConfigPath = Path.Combine(
            world.WorkingDirectory,
            ConfigDirectoryName,
            ConfigFileName);
        if (!File.Exists(workspaceConfigPath))
        {
            return;
        }

        if (world.Installation.Metadata is null ||
            !world.Installation.Metadata.TryGetValue(
                FactorioInstallationDiscovery.UserDataPathKey,
                out var userDataPath) ||
            string.IsNullOrWhiteSpace(userDataPath))
        {
            return;
        }

        var playerConfigPath = Path.Combine(
            userDataPath,
            ConfigDirectoryName,
            ConfigFileName);
        if (!File.Exists(playerConfigPath))
        {
            return;
        }

        string? temporaryPath = null;
        try
        {
            var workspaceLines = await File.ReadAllLinesAsync(workspaceConfigPath, cancellationToken);
            var playerLines = await File.ReadAllLinesAsync(playerConfigPath, cancellationToken);
            var merged = MergePlayerPreferences(workspaceLines, playerLines);

            temporaryPath = Path.Combine(
                Path.GetDirectoryName(playerConfigPath)!,
                $".{ConfigFileName}.sharedworlds-{Guid.NewGuid():N}.tmp");
            await File.WriteAllLinesAsync(temporaryPath, merged, cancellationToken);
            File.Move(temporaryPath, playerConfigPath, overwrite: true);
            temporaryPath = null;
        }
        catch (IOException)
        {
            // Player preferences are non-canonical convenience state. A failure to persist them
            // must never invalidate an otherwise successful World commit or recovery decision.
        }
        catch (UnauthorizedAccessException)
        {
            // The World lifecycle must not fail because a local preference file is read-only.
        }
        finally
        {
            if (temporaryPath is not null)
            {
                TryDeleteFile(temporaryPath);
            }
        }
    }

    internal static IReadOnlyList<string> MergePlayerPreferences(
        IReadOnlyList<string> workspaceLines,
        IReadOnlyList<string> currentPlayerLines)
    {
        var playerPathSection = ExtractSection(currentPlayerLines, PathSectionName);
        var result = new List<string>(workspaceLines.Count + playerPathSection.Count);
        var skippingWorkspacePathSection = false;
        var pathSectionHandled = false;

        foreach (var line in workspaceLines)
        {
            if (TryGetSectionName(line, out var sectionName))
            {
                if (string.Equals(sectionName, PathSectionName, StringComparison.OrdinalIgnoreCase))
                {
                    if (!pathSectionHandled && playerPathSection.Count > 0)
                    {
                        result.AddRange(playerPathSection);
                    }

                    pathSectionHandled = true;
                    skippingWorkspacePathSection = true;
                    continue;
                }

                skippingWorkspacePathSection = false;
                result.Add(line);
                continue;
            }

            if (!skippingWorkspacePathSection)
            {
                result.Add(line);
            }
        }

        return result;
    }

    private static IReadOnlyList<string> ExtractSection(
        IReadOnlyList<string> lines,
        string requestedSection)
    {
        var result = new List<string>();
        var inRequestedSection = false;

        foreach (var line in lines)
        {
            if (TryGetSectionName(line, out var sectionName))
            {
                if (inRequestedSection)
                {
                    break;
                }

                inRequestedSection = string.Equals(
                    sectionName,
                    requestedSection,
                    StringComparison.OrdinalIgnoreCase);
                if (inRequestedSection)
                {
                    result.Add(line);
                }

                continue;
            }

            if (inRequestedSection)
            {
                result.Add(line);
            }
        }

        return result;
    }

    private static bool TryGetSectionName(string line, out string sectionName)
    {
        var trimmed = line.Trim();
        if (trimmed.Length >= 3 &&
            trimmed[0] == '[' &&
            trimmed[^1] == ']')
        {
            sectionName = trimmed[1..^1].Trim();
            return sectionName.Length > 0;
        }

        sectionName = string.Empty;
        return false;
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
            // Best-effort cleanup of a temporary preference file.
        }
        catch (UnauthorizedAccessException)
        {
            // Best-effort cleanup of a temporary preference file.
        }
    }

    private static void DeleteOwnedWorkspace(string workingDirectory)
    {
        FactorioWorkspaceOwnership.RequireOwned(workingDirectory);
        var fullPath = Path.GetFullPath(workingDirectory);
        if (Directory.Exists(fullPath))
        {
            Directory.Delete(fullPath, recursive: true);
        }
    }

    private sealed record FactorioLaunchObservation(
        string ProcessName,
        IReadOnlySet<int> BaselineProcessIds);
}
