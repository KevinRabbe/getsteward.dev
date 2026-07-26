using System.Reflection;

namespace SharedWorlds.Desktop;

internal static class StewardBuildVersion
{
    public static string Current { get; } = Resolve();

    private static string Resolve()
    {
        var version = typeof(App).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;
        return string.IsNullOrWhiteSpace(version)
            ? "development"
            : version;
    }
}
