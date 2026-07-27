using Xunit;

namespace SharedWorlds.Desktop.AcceptanceTests;

public sealed class PortableWorldStartupActivationTests
{
    [Fact]
    public void ResolvesOnePortableWorldArgumentToFullPath()
    {
        var relative = Path.Combine("downloads", "500h-megabase.safeworld");

        var resolved = PortableWorldStartupActivation.ResolvePath([relative]);

        Assert.Equal(Path.GetFullPath(relative), resolved);
    }

    [Fact]
    public void ExtensionMatchIsCaseInsensitive()
    {
        var relative = Path.Combine("downloads", "500h-megabase.SAFEWORLD");

        var resolved = PortableWorldStartupActivation.ResolvePath([relative]);

        Assert.Equal(Path.GetFullPath(relative), resolved);
    }

    [Theory]
    [InlineData()]
    [InlineData("not-a-world.txt")]
    [InlineData("one.safeworld", "two.safeworld")]
    public void IgnoresArgumentsThatAreNotOnePortableWorld(params string[] arguments)
        => Assert.Null(PortableWorldStartupActivation.ResolvePath(arguments));

    [Fact]
    public void InvalidPathDoesNotCrashStartupParsing()
        => Assert.Null(PortableWorldStartupActivation.ResolvePath(["bad\0path.safeworld"]));
}
