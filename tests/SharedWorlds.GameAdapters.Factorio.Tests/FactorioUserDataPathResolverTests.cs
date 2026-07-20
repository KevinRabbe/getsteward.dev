using SharedWorlds.GameAdapters.Factorio;

namespace SharedWorlds.GameAdapters.Factorio.Tests;

public sealed class FactorioUserDataPathResolverTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"sharedworlds-factorio-path-tests-{Guid.NewGuid():N}");

    [Fact]
    public void Resolve_PrefersConfiguredWriteDataOverPortableSaves()
    {
        var installation = CreateInstallation();
        Directory.CreateDirectory(Path.Combine(installation.Root, "saves"));
        var configured = Path.Combine(_root, "custom-user-data");
        WriteConfig(
            Path.Combine(installation.Root, "config", "config.ini"),
            configured);

        var resolved = Resolve(installation);

        Assert.Equal(Path.GetFullPath(configured), resolved);
    }

    [Fact]
    public void Resolve_ExpandsExecutablePathToken()
    {
        var installation = CreateInstallation();
        WriteConfig(
            Path.Combine(installation.Root, "config", "config.ini"),
            "__PATH__executable__/../..");

        var resolved = Resolve(installation);

        Assert.Equal(Path.GetFullPath(installation.Root), resolved);
    }

    [Fact]
    public void Resolve_UsesConfigPathCfgToFindConfigFile()
    {
        var installation = CreateInstallation();
        var alternateConfigDirectory = Path.Combine(installation.Root, "alternate-config");
        var configuredUserData = Path.Combine(_root, "cfg-selected-user-data");

        Directory.CreateDirectory(installation.Root);
        File.WriteAllText(
            Path.Combine(installation.Root, "config-path.cfg"),
            "config-path=__PATH__executable__/../../alternate-config");
        WriteConfig(
            Path.Combine(alternateConfigDirectory, "config.ini"),
            configuredUserData);

        var resolved = Resolve(installation);

        Assert.Equal(Path.GetFullPath(configuredUserData), resolved);
    }

    [Fact]
    public void Resolve_RelativeWriteDataAgainstUserProfile()
    {
        var installation = CreateInstallation();
        WriteConfig(
            Path.Combine(installation.Root, "config", "config.ini"),
            ".local/share/factorio-custom");

        var resolved = Resolve(installation);

        Assert.Equal(
            Path.GetFullPath(Path.Combine(installation.UserProfile, ".local", "share", "factorio-custom")),
            resolved);
    }

    [Fact]
    public void Resolve_ExpandsSystemWriteDataToken()
    {
        var installation = CreateInstallation();
        WriteConfig(
            Path.Combine(installation.Root, "config", "config.ini"),
            "__PATH__system-write-data__");

        var resolved = Resolve(installation);

        Assert.Equal(Path.GetFullPath(installation.DefaultUserData), resolved);
    }

    [Fact]
    public void Resolve_FallsBackToPortableThenSystemDefault()
    {
        var portable = CreateInstallation("portable");
        Directory.CreateDirectory(Path.Combine(portable.Root, "saves"));
        Assert.Equal(Path.GetFullPath(portable.Root), Resolve(portable));

        var installed = CreateInstallation("installed");
        Assert.Equal(Path.GetFullPath(installed.DefaultUserData), Resolve(installed));
    }

    private TestInstallation CreateInstallation(string name = "factorio")
    {
        var root = Path.Combine(_root, name);
        var executable = Path.Combine(root, "bin", "x64", "factorio.exe");
        var defaultUserData = Path.Combine(_root, $"{name}-system-user-data");
        var userProfile = Path.Combine(_root, $"{name}-home");
        Directory.CreateDirectory(Path.GetDirectoryName(executable)!);
        File.WriteAllText(executable, string.Empty);
        Directory.CreateDirectory(userProfile);
        return new TestInstallation(root, executable, defaultUserData, userProfile);
    }

    private static string Resolve(TestInstallation installation)
        => FactorioUserDataPathResolver.Resolve(
            installation.Root,
            installation.Executable,
            installation.DefaultUserData,
            installation.UserProfile);

    private static void WriteConfig(string path, string writeData)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllLines(
            path,
            [
                "[path]",
                "read-data=__PATH__executable__/../../data",
                $"write-data={writeData}"
            ]);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
            // Test cleanup must not hide the assertion result.
        }
        catch (UnauthorizedAccessException)
        {
            // Test cleanup must not hide the assertion result.
        }
    }

    private sealed record TestInstallation(
        string Root,
        string Executable,
        string DefaultUserData,
        string UserProfile);
}
