using System.Collections.Concurrent;
using System.Diagnostics;
using SharedWorlds.Core.Abstractions;
using SharedWorlds.Core.Environment;

namespace SharedWorlds.GameAdapters.Factorio;

public sealed class FactorioAdapter : IGameAdapter
{
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
        GameAdapterCapabilities.ExactGameVersion;

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

    public async Task<CapturedState> CaptureDetectedWorldAsync(
        GameInstallation installation,
        DetectedWorld world,
        CancellationToken cancellationToken = default)
    {
        var captured = await FactorioWorldOperations.CaptureDetectedWorldAsync(world, cancellationToken);
        return captured with { DeletePackageAfterStore = true };
    }

    public Task<PreparedWorld> PrepareEnvironmentAsync(
        GameInstallation installation,
        EnvironmentManifest requiredEnvironment,
        CancellationToken cancellationToken = default)
        => FactorioWorldOperations.PrepareEnvironmentAsync(
            installation,
            requiredEnvironment,
            cancellationToken);

    public async Task<CapturedState> CaptureStateAsync(
        PreparedWorld world,
        CancellationToken cancellationToken = default)
    {
        var captured = await FactorioWorldOperations.CaptureStateAsync(world, cancellationToken);
        return captured with { DeletePackageAfterStore = true };
    }

    public Task RestoreStateAsync(
        PreparedWorld world,
        StatePackage state,
        CancellationToken cancellationToken = default)
        => FactorioWorldOperations.RestoreStateAsync(world, state, cancellationToken);

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

    public Task FinalizePreparedWorldAsync(
        PreparedWorld world,
        PreparedWorldDisposition disposition,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (disposition == PreparedWorldDisposition.PreserveForRecovery)
        {
            return Task.CompletedTask;
        }

        DeleteOwnedWorkspace(world.WorkingDirectory);
        return Task.CompletedTask;
    }

    private async Task<GameSessionHandle> LaunchTrackedAsync(
        PreparedWorld world,
        Func<PreparedWorld, CancellationToken, Task<GameSessionHandle>> launch,
        CancellationToken cancellationToken)
    {
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

    private static void DeleteOwnedWorkspace(string workingDirectory)
    {
        var fullPath = Path.GetFullPath(workingDirectory);
        var parent = Directory.GetParent(fullPath)
            ?? throw new InvalidOperationException(
                $"Cannot determine parent directory for Factorio workspace '{workingDirectory}'.");
        if (!Guid.TryParseExact(Path.GetFileName(fullPath), "N", out _) ||
            !string.Equals(parent.Name, "factorio", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(parent.Parent?.Name, "SharedWorlds", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Refusing to recursively delete unrecognized Factorio workspace '{workingDirectory}'.");
        }

        if (Directory.Exists(fullPath))
        {
            Directory.Delete(fullPath, recursive: true);
        }
    }

    private sealed record FactorioLaunchObservation(
        string ProcessName,
        IReadOnlySet<int> BaselineProcessIds);
}
