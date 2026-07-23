using System.Diagnostics;
using SharedWorlds.Core.Abstractions;

namespace SharedWorlds.GameAdapters.Palworld;

internal static partial class PalworldDedicatedServerHosting
{
    internal static PalworldManagedHostLaunchContext PrepareManagedHostLaunch(PreparedWorld world)
    {
        ArgumentNullException.ThrowIfNull(world);
        EnsureSupportedPlatform();
        EnsureDedicatedServerAvailable(world.Installation);

        if (!world.Environment.Configuration.TryGetValue(DedicatedServerNameKey, out var worldId) ||
            string.IsNullOrWhiteSpace(worldId))
        {
            throw new InvalidOperationException(
                $"Prepared Palworld environment is missing '{DedicatedServerNameKey}'.");
        }

        ValidateWorldId(worldId);
        var serverRoot = GetRequiredMetadata(
            world.Installation,
            PalworldInstallationDiscovery.DedicatedServerRootPathKey);
        var serverExecutable = GetRequiredMetadata(
            world.Installation,
            PalworldInstallationDiscovery.DedicatedServerExecutablePathKey);
        var expectedWorldPath = GetDedicatedWorldPath(serverRoot, worldId);
        var actualWorldPath = Path.GetFullPath(world.WorkingDirectory);
        var pathComparer = OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;

        if (!pathComparer.Equals(expectedWorldPath, actualWorldPath))
        {
            throw new InvalidOperationException(
                $"Prepared Palworld world path does not match the required dedicated world '{worldId}'.");
        }

        if (!File.Exists(Path.Combine(actualWorldPath, LevelSaveFileName)))
        {
            throw new InvalidOperationException(
                $"The prepared Palworld dedicated world has no {LevelSaveFileName}: {actualWorldPath}");
        }

        SelectDedicatedWorld(serverRoot, worldId);
        return new PalworldManagedHostLaunchContext(
            serverRoot,
            serverExecutable,
            worldId,
            actualWorldPath);
    }

    internal static Process StartManagedHost(PalworldManagedHostLaunchContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return Process.Start(new ProcessStartInfo
        {
            FileName = context.ServerExecutable,
            WorkingDirectory = context.ServerRoot,
            UseShellExecute = false
        }) ?? throw new InvalidOperationException("Palworld dedicated server failed to start.");
    }
}

internal sealed record PalworldManagedHostLaunchContext(
    string ServerRoot,
    string ServerExecutable,
    string WorldId,
    string WorldPath);
