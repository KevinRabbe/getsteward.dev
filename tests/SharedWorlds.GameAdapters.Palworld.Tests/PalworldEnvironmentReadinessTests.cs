using SharedWorlds.Core.Abstractions;
using SharedWorlds.Core.Environment;
using SharedWorlds.GameAdapters.Palworld;
using Xunit;

namespace SharedWorlds.GameAdapters.Palworld.Tests;

public sealed class PalworldEnvironmentReadinessTests
{
    [Fact]
    public async Task InspectEnvironmentCapturesExactDedicatedServerSteamBuildId()
    {
        using var fixture = new PalworldFixture("12345678");
        var adapter = new PalworldAdapter();

        var manifest = await adapter.InspectEnvironmentAsync(
            fixture.Installation,
            fixture.World);

        Assert.Equal("palworld", manifest.AdapterId);
        Assert.Equal("12345678", manifest.GameVersion);
        Assert.Equal("dedicated-server", manifest.Configuration["hostingMode"]);
        Assert.Equal(fixture.WorldId, manifest.Configuration["dedicatedServerName"]);
        Assert.True(adapter.Capabilities.HasFlag(GameAdapterCapabilities.ExactGameVersion));
    }

    [Fact]
    public async Task VerifyEnvironmentRequiresTheExactDedicatedServerBuildOnWindows()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var fixture = new PalworldFixture("12345678");
        var adapter = new PalworldAdapter();
        var required = await adapter.InspectEnvironmentAsync(fixture.Installation, fixture.World);

        var ready = await adapter.VerifyEnvironmentAsync(fixture.Installation, required);
        Assert.True(ready.IsReady);

        fixture.WriteServerManifest("87654321");
        var mismatch = await adapter.VerifyEnvironmentAsync(fixture.Installation, required);

        Assert.Equal(EnvironmentVerificationState.Blocked, mismatch.State);
        var issue = Assert.Single(mismatch.Issues.Where(issue => issue.Code == "palworld-version-mismatch"));
        Assert.Contains("12345678", issue.Message, StringComparison.Ordinal);
        Assert.Contains("87654321", issue.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task VerifyEnvironmentFailsClosedWhenCanonicalBuildIsUnknownOnWindows()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var fixture = new PalworldFixture("12345678");
        var adapter = new PalworldAdapter();
        var required = new EnvironmentManifest(
            SchemaVersion: 1,
            AdapterId: "palworld",
            GameVersion: "unknown",
            Components: [],
            Configuration: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["hostingMode"] = "dedicated-server",
                ["dedicatedServerName"] = fixture.WorldId
            });

        var verification = await adapter.VerifyEnvironmentAsync(fixture.Installation, required);

        Assert.Equal(EnvironmentVerificationState.Blocked, verification.State);
        Assert.Contains(
            verification.Issues,
            issue => issue.Code == "palworld-exact-version-unavailable");
    }

    [Fact]
    public async Task VerifyEnvironmentRequiresInitializedDedicatedServerConfigOnWindows()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var fixture = new PalworldFixture("12345678");
        var adapter = new PalworldAdapter();
        var required = await adapter.InspectEnvironmentAsync(fixture.Installation, fixture.World);
        File.Delete(fixture.ServerConfigPath);

        var verification = await adapter.VerifyEnvironmentAsync(fixture.Installation, required);

        Assert.Equal(EnvironmentVerificationState.Blocked, verification.State);
        Assert.Contains(
            verification.Issues,
            issue => issue.Code == "palworld-server-not-initialized");
    }

    private sealed class PalworldFixture : IDisposable
    {
        private readonly string _root;

        public PalworldFixture(string serverBuildId)
        {
            _root = Path.Combine(Path.GetTempPath(), "steward-palworld-tests", Guid.NewGuid().ToString("N"));
            var clientRoot = Path.Combine(_root, "Palworld");
            var serverRoot = Path.Combine(_root, "PalServer");
            var serverExecutable = Path.Combine(serverRoot, "PalServer.exe");
            var serverManifest = Path.Combine(_root, "appmanifest_2394010.acf");
            WorldId = "0123456789ABCDEF0123456789ABCDEF";
            var worldPath = Path.Combine(clientRoot, "Pal", "Saved", "SaveGames", "76561198000000000", WorldId);
            ServerConfigPath = Path.Combine(
                serverRoot,
                "Pal",
                "Saved",
                "Config",
                "WindowsServer",
                "GameUserSettings.ini");

            Directory.CreateDirectory(worldPath);
            Directory.CreateDirectory(serverRoot);
            Directory.CreateDirectory(Path.GetDirectoryName(ServerConfigPath)!);
            File.WriteAllText(serverExecutable, string.Empty);
            File.WriteAllText(ServerConfigPath, "DedicatedServerName=placeholder\r\n");
            WriteServerManifest(serverBuildId);

            Installation = new GameInstallation(
                Id: $"palworld:{clientRoot}",
                RootPath: clientRoot,
                Source: "steam",
                Metadata: new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["clientExecutablePath"] = Path.Combine(clientRoot, "Palworld.exe"),
                    ["gameSteamAppId"] = "1623730",
                    ["dedicatedServerSteamAppId"] = "2394010",
                    ["dedicatedServerInstallState"] = "installed",
                    ["dedicatedServerRootPath"] = serverRoot,
                    ["dedicatedServerExecutablePath"] = serverExecutable,
                    ["dedicatedServerManifestPath"] = serverManifest
                });
            World = new DetectedWorld(
                Id: $"local:76561198000000000:{WorldId}",
                DisplayName: "Palworld readiness test",
                SourcePath: worldPath);
            ServerManifestPath = serverManifest;
        }

        public GameInstallation Installation { get; }
        public DetectedWorld World { get; }
        public string WorldId { get; }
        public string ServerConfigPath { get; }
        public string ServerManifestPath { get; }

        public void WriteServerManifest(string buildId)
            => File.WriteAllText(
                ServerManifestPath,
                $"\"AppState\"\r\n{{\r\n    \"appid\" \"2394010\"\r\n    \"buildid\" \"{buildId}\"\r\n}}");

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
                // Best-effort test cleanup.
            }
            catch (UnauthorizedAccessException)
            {
                // Best-effort test cleanup.
            }
        }
    }
}
