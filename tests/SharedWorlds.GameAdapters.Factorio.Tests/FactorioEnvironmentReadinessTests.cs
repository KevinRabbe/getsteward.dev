using SharedWorlds.Core.Abstractions;
using SharedWorlds.Core.Environment;
using SharedWorlds.GameAdapters.Factorio;

namespace SharedWorlds.GameAdapters.Factorio.Tests;

public sealed class FactorioEnvironmentReadinessTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"sharedworlds-factorio-readiness-tests-{Guid.NewGuid():N}");

    [Fact]
    public async Task VerifyEnvironment_ReturnsReady_WhenExactEnvironmentCanBePrepared()
    {
        var installation = CreateInstallation("2.1.11");
        var adapter = new FactorioAdapter();
        var manifest = CreateManifest(adapter, "2.1.11");

        var result = await adapter.VerifyEnvironmentAsync(installation, manifest);

        Assert.True(result.IsReady);
        Assert.Equal(EnvironmentVerificationState.Ready, result.State);
        Assert.Empty(result.Issues);
    }

    [Fact]
    public async Task VerifyEnvironment_ReturnsBlocked_WhenInstalledVersionDoesNotMatch()
    {
        var installation = CreateInstallation("2.0.72");
        var adapter = new FactorioAdapter();
        var manifest = CreateManifest(adapter, "2.1.11");

        var result = await adapter.VerifyEnvironmentAsync(installation, manifest);

        Assert.Equal(EnvironmentVerificationState.Blocked, result.State);
        Assert.False(result.CanRepairAutomatically);
        var issue = Assert.Single(result.Issues);
        Assert.Contains("2.0.72", issue.Message, StringComparison.Ordinal);
        Assert.Contains("2.1.11", issue.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RepairEnvironment_DoesNotMutate_WhenNoSafeRepairPathExists()
    {
        var installation = CreateInstallation("2.0.72");
        var adapter = new FactorioAdapter();
        var manifest = CreateManifest(adapter, "2.1.11");

        var result = await adapter.RepairEnvironmentAsync(installation, manifest);

        Assert.False(result.Changed);
        Assert.Equal(EnvironmentVerificationState.Blocked, result.Verification.State);
        Assert.Contains("Nothing was changed", result.Message, StringComparison.Ordinal);
    }

    private GameInstallation CreateInstallation(string gameVersion)
    {
        var installationRoot = Path.Combine(_root, "installation");
        var userDataPath = Path.Combine(_root, "user-data");
        var baseDirectory = Path.Combine(installationRoot, "data", "base");
        Directory.CreateDirectory(baseDirectory);
        Directory.CreateDirectory(userDataPath);
        File.WriteAllText(
            Path.Combine(baseDirectory, "info.json"),
            $$"""
            {
              "name": "base",
              "version": "{{gameVersion}}"
            }
            """);

        return new GameInstallation(
            Id: "test-factorio",
            RootPath: installationRoot,
            Source: "test",
            Metadata: new Dictionary<string, string>
            {
                ["userDataPath"] = userDataPath,
                ["executablePath"] = Path.Combine(installationRoot, "factorio.exe")
            });
    }

    private static EnvironmentManifest CreateManifest(FactorioAdapter adapter, string gameVersion)
        => new(
            SchemaVersion: 1,
            AdapterId: adapter.Id,
            GameVersion: gameVersion,
            Components: [],
            Configuration: new Dictionary<string, string>());

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
            // Test cleanup must not hide assertion failures.
        }
        catch (UnauthorizedAccessException)
        {
            // Test cleanup must not hide assertion failures.
        }
    }
}
