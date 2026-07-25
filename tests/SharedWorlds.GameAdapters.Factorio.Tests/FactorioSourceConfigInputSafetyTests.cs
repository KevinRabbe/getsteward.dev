using SharedWorlds.Core.Abstractions;
using SharedWorlds.Core.Environment;

namespace SharedWorlds.GameAdapters.Factorio.Tests;

public sealed class FactorioSourceConfigInputSafetyTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"sharedworlds-factorio-source-config-safety-{Guid.NewGuid():N}");

    [Fact]
    public async Task ReproductionRejectsLinkedSourceConfigDirectoryBeforeRead()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var userData = Path.Combine(_root, "linked-config-directory-user-data");
        Directory.CreateDirectory(userData);
        var outsideConfig = Path.Combine(_root, "outside-config-directory");
        Directory.CreateDirectory(outsideConfig);
        await File.WriteAllTextAsync(
            Path.Combine(outsideConfig, "config.ini"),
            "[path]\nwrite-data=C:\\outside");
        var linkedConfig = Path.Combine(userData, "config");
        Directory.CreateSymbolicLink(linkedConfig, outsideConfig);

        try
        {
            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                FactorioWorldOperations.PrepareEnvironmentAsync(
                    Installation(userData),
                    RequiredEnvironment(),
                    CancellationToken.None));

            Assert.Contains("source config directory", exception.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("linked or a reparse point", exception.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(linkedConfig);
        }
    }

    [Fact]
    public async Task ReproductionRejectsLinkedSourceConfigFileBeforeRead()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var userData = Path.Combine(_root, "linked-config-file-user-data");
        var configDirectory = Path.Combine(userData, "config");
        Directory.CreateDirectory(configDirectory);
        var outsideConfig = Path.Combine(_root, "outside-config.ini");
        await File.WriteAllTextAsync(outsideConfig, "[path]\nwrite-data=C:\\outside");
        var linkedConfig = Path.Combine(configDirectory, "config.ini");
        File.CreateSymbolicLink(linkedConfig, outsideConfig);

        try
        {
            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                FactorioWorldOperations.PrepareEnvironmentAsync(
                    Installation(userData),
                    RequiredEnvironment(),
                    CancellationToken.None));

            Assert.Contains("source config file", exception.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("linked or a reparse point", exception.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            File.Delete(linkedConfig);
        }
    }

    [Fact]
    public async Task ReproductionRejectsOversizedSourceConfigBeforeAllocation()
    {
        var userData = Path.Combine(_root, "oversized-config-user-data");
        var configDirectory = Path.Combine(userData, "config");
        Directory.CreateDirectory(configDirectory);
        var configPath = Path.Combine(configDirectory, "config.ini");
        using (var stream = new FileStream(
                   configPath,
                   FileMode.CreateNew,
                   FileAccess.Write,
                   FileShare.None))
        {
            stream.SetLength(FactorioWorldOperations.MaximumSourceConfigBytes + 1L);
        }

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            FactorioWorldOperations.PrepareEnvironmentAsync(
                Installation(userData),
                RequiredEnvironment(),
                CancellationToken.None));

        Assert.Contains("source config file", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("safety limit", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(
            FactorioWorldOperations.MaximumSourceConfigBytes + 1L,
            new FileInfo(configPath).Length);
    }

    private static GameInstallation Installation(string userData)
        => new(
            "factorio:test",
            Path.Combine(Path.GetTempPath(), "factorio-test-install"),
            "test",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [FactorioInstallationDiscovery.UserDataPathKey] = userData
            });

    private static EnvironmentManifest RequiredEnvironment()
        => new(
            1,
            "factorio",
            "test-version",
            [],
            new Dictionary<string, string>());

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
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
