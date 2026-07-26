using System.Reflection;

namespace SharedWorlds.Desktop.AcceptanceTests;

public sealed class StewardBuildVersionTests
{
    [Fact]
    public void CurrentBuildVersionMatchesDesktopInformationalVersion()
    {
        var expected = typeof(App).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;

        Assert.False(string.IsNullOrWhiteSpace(expected));
        Assert.Equal(expected, StewardBuildVersion.Current);
        Assert.DoesNotContain("\r", StewardBuildVersion.Current, StringComparison.Ordinal);
        Assert.DoesNotContain("\n", StewardBuildVersion.Current, StringComparison.Ordinal);
    }
}
