using SharedWorlds.Core.Abstractions;
using SharedWorlds.Core.Environment;
using SharedWorlds.GameAdapters.Factorio;

namespace SharedWorlds.GameAdapters.Factorio.Tests;

public sealed class FactorioDedicatedServerTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "steward-factorio-dedicated-tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public void HostCommandUsesDedicatedStartServerAndWorkspaceBoundaries()
    {
        var world = CreatePreparedWorld();
        var consoleLog = Path.Combine(world.WorkingDirectory, "server-console.log");

        var startInfo = FactorioDedicatedServer.CreateStartInfo(world, consoleLog);
        var arguments = startInfo.ArgumentList.ToArray();

        Assert.Contains("--start-server", arguments);
        Assert.DoesNotContain("--host", arguments);
        Assert.Contains("--console-log", arguments);
        Assert.Contains(Path.GetFullPath(consoleLog), arguments);
        Assert.Contains(
            Path.Combine(world.WorkingDirectory, "config", "config.ini"),
            arguments);
        Assert.Contains(
            Path.Combine(world.WorkingDirectory, "mods"),
            arguments);
        Assert.Contains(
            Path.Combine(world.WorkingDirectory, "user-data", "saves", "world.zip"),
            arguments);
    }

    [Theory]
    [InlineData("4.701 Hosting game at IP ADDR:({0.0.0.0:34197})")]
    [InlineData("2.710 Hosting game at 0.0.0.0:34197")]
    public void HostingLogMarkerProvesServerSocketReachedHostingState(string line)
        => Assert.True(FactorioDedicatedServer.IsReadyLogLine(line));

    [Theory]
    [InlineData("")]
    [InlineData("Info UDPSocket.cpp:32: Opening socket at 0.0.0.0:34197")]
    [InlineData("Loading map world.zip")]
    [InlineData("Factorio process started")]
    public void EarlierStartupLinesDoNotCountAsServerReady(string line)
        => Assert.False(FactorioDedicatedServer.IsReadyLogLine(line));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private PreparedWorld CreatePreparedWorld()
    {
        var workspace = Path.Combine(_root, "workspace");
        Directory.CreateDirectory(Path.Combine(workspace, "config"));
        Directory.CreateDirectory(Path.Combine(workspace, "mods"));
        Directory.CreateDirectory(Path.Combine(workspace, "user-data", "saves"));
        File.WriteAllText(Path.Combine(workspace, "config", "config.ini"), "[path]");

        var executable = Path.Combine(_root, "Factorio", "bin", "x64", "factorio.exe");
        var installation = new GameInstallation(
            "factorio:test",
            Path.Combine(_root, "Factorio"),
            "test",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [FactorioInstallationDiscovery.ExecutablePathKey] = executable,
                [FactorioInstallationDiscovery.UserDataPathKey] = Path.Combine(_root, "user-data")
            });
        var manifest = new EnvironmentManifest(
            1,
            "factorio",
            "2.0.0",
            [],
            new Dictionary<string, string>());
        return new PreparedWorld(installation, workspace, manifest, "Dedicated Test World");
    }
}
