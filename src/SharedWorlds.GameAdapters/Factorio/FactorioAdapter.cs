using System.Diagnostics;
using SharedWorlds.Core.Abstractions;
using SharedWorlds.Core.Environment;

namespace SharedWorlds.GameAdapters.Factorio;

public sealed class FactorioAdapter : IGameAdapter
{
    public string Id => "factorio";
    public string DisplayName => "Factorio";

    public GameAdapterCapabilities Capabilities =>
        GameAdapterCapabilities.Mods |
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

    public Task<GameSessionHandle> LaunchHostAsync(
        PreparedWorld world,
        CancellationToken cancellationToken = default)
        => FactorioWorldOperations.LaunchHostAsync(world, cancellationToken);

    public Task<GameSessionHandle> LaunchClientAsync(
        PreparedWorld world,
        HostConnection host,
        CancellationToken cancellationToken = default)
        => FactorioWorldOperations.LaunchClientAsync(world, host, cancellationToken);

    public async Task WaitForSessionEndAsync(
        GameSessionHandle session,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var process = Process.GetProcessById(session.ProcessId);
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (ArgumentException)
        {
            // The process already exited before we started observing it.
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
}
