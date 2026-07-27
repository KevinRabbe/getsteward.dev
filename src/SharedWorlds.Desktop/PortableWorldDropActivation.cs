using System.IO;

namespace SharedWorlds.Desktop;

internal static class PortableWorldDropActivation
{
    internal static string? ResolvePath(IReadOnlyList<string> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        if (paths.Count != 1 || string.IsNullOrWhiteSpace(paths[0]))
        {
            return null;
        }

        return PortableWorldStartupActivation.ResolvePath([paths[0]]);
    }
}
