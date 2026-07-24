using SharedWorlds.Core.Abstractions;

namespace SharedWorlds.GameAdapters.Palworld;

internal static class PalworldWorkspaceOwnership
{
    private const string DedicatedServerNameKey = "dedicatedServerName";

    public static void RequireOwned(PreparedWorld world)
    {
        ArgumentNullException.ThrowIfNull(world);

        if (!world.Environment.Configuration.TryGetValue(DedicatedServerNameKey, out var worldId) ||
            string.IsNullOrWhiteSpace(worldId) ||
            !IsSafeWorldId(worldId))
        {
            throw Refuse(world.WorkingDirectory);
        }

        if (world.Installation.Metadata is null ||
            !world.Installation.Metadata.TryGetValue(
                PalworldInstallationDiscovery.DedicatedServerRootPathKey,
                out var serverRoot) ||
            string.IsNullOrWhiteSpace(serverRoot))
        {
            throw Refuse(world.WorkingDirectory);
        }

        var expected = Path.GetFullPath(Path.Combine(
            serverRoot,
            "Pal",
            "Saved",
            "SaveGames",
            "0",
            worldId));
        var actual = Path.GetFullPath(world.WorkingDirectory);
        if (!string.Equals(expected, actual, PathComparison))
        {
            throw Refuse(world.WorkingDirectory);
        }
    }

    private static bool IsSafeWorldId(string worldId)
        => !string.Equals(worldId, ".", StringComparison.Ordinal) &&
           !string.Equals(worldId, "..", StringComparison.Ordinal) &&
           !Path.IsPathRooted(worldId) &&
           string.Equals(Path.GetFileName(worldId), worldId, StringComparison.Ordinal) &&
           worldId.IndexOfAny(Path.GetInvalidFileNameChars()) < 0;

    private static StringComparison PathComparison => OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;

    private static InvalidOperationException Refuse(string workingDirectory)
        => new(
            $"Refusing to use unrecognized Palworld dedicated World path '{workingDirectory}'.");
}
