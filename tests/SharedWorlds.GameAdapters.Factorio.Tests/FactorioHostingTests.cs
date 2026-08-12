using System.Text.Json;
using SharedWorlds.Core.Abstractions;
using SharedWorlds.Core.Environment;
using SharedWorlds.GameAdapters.Factorio;

namespace SharedWorlds.GameAdapters.Factorio.Tests;

public sealed class FactorioHostingTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"sharedworlds-factorio-hosting-tests-{Guid.NewGuid():N}");

    [Fact]
    public void BuildClientOperationArguments_IncludesAddressPortAndOpaquePassword()
    {
        var arguments = FactorioHostingOperations.BuildClientOperationArguments(
            new HostConnection("127.0.0.1", 34197, "session-secret"));

        Assert.Equal(
            ["--mp-connect", "127.0.0.1:34197", "--password", "session-secret"],
            arguments);
    }

    [Fact]
    public void BuildClientOperationArguments_OmitsPasswordWhenNoCredentialExists()
    {
        var arguments = FactorioHostingOperations.BuildClientOperationArguments(
            new HostConnection("example.test", 34197));

        Assert.Equal(["--mp-connect", "example.test:34197"], arguments);
    }

    [Fact]
    public async Task Adapter_AdvertisesAndImplementsManagedHostStop()
    {
        var adapter = new FactorioAdapter();
        Assert.True(
            adapter.Capabilities.HasFlag(GameAdapterCapabilities.AutomaticHostStop));

        var contract = (IGameAdapter)adapter;
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            contract.RequestHostStopAsync(
                new GameSessionHandle(int.MaxValue, DateTimeOffset.UtcNow),
                CancellationToken.None));

        Assert.Contains(
            "running Steward-managed Host session",
            exception.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task CreatePrivateServerSettings_HidesServerAndRequiresGeneratedPassword()
    {
        var installationRoot = Path.Combine(_root, "Factorio");
        var dataDirectory = Path.Combine(installationRoot, "data");
        var workspace = Path.Combine(_root, "workspace");
        Directory.CreateDirectory(dataDirectory);
        Directory.CreateDirectory(workspace);

        await File.WriteAllTextAsync(
            Path.Combine(dataDirectory, "server-settings.example.json"),
            """
            {
              "name": "example",
              "description": "",
              "visibility": {
                "public": true,
                "lan": true,
                "steam": true
              },
              "game_password": "",
              "require_user_verification": true
            }
            """);

        var world = new PreparedWorld(
            new GameInstallation("factorio-test", installationRoot, "test"),
            workspace,
            new EnvironmentManifest(
                SchemaVersion: 1,
                AdapterId: "factorio",
                GameVersion: "2.1.11",
                Components: [],
                Configuration: new Dictionary<string, string>()),
            DisplayName: "newme");
        var destination = Path.Combine(workspace, "host", "server-settings.json");

        await FactorioHostingOperations.CreatePrivateServerSettingsAsync(
            world,
            destination,
            "generated-secret",
            CancellationToken.None);

        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(destination));
        var root = document.RootElement;
        Assert.Equal("newme", root.GetProperty("name").GetString());
        Assert.Equal("generated-secret", root.GetProperty("game_password").GetString());
        Assert.False(root.GetProperty("require_user_verification").GetBoolean());
        Assert.False(root.GetProperty("visibility").GetProperty("public").GetBoolean());
        Assert.False(root.GetProperty("visibility").GetProperty("lan").GetBoolean());
        Assert.False(root.GetProperty("visibility").GetProperty("steam").GetBoolean());
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
            // Test cleanup must not hide the actual assertion result.
        }
        catch (UnauthorizedAccessException)
        {
            // Test cleanup must not hide the actual assertion result.
        }
    }
}
