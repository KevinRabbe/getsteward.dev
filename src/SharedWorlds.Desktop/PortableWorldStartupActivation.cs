using System.IO;

namespace SharedWorlds.Desktop;

internal static class PortableWorldStartupActivation
{
    internal static string? ResolvePath(IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        if (arguments.Count != 1 || string.IsNullOrWhiteSpace(arguments[0]))
        {
            return null;
        }

        try
        {
            var fullPath = Path.GetFullPath(arguments[0]);
            return string.Equals(
                Path.GetExtension(fullPath),
                PortableWorldFileAssociation.Extension,
                StringComparison.OrdinalIgnoreCase)
                ? fullPath
                : null;
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }
}
