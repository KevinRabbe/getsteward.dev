using SharedWorlds.Core.Abstractions;
using SharedWorlds.GameAdapters.Smalland;
using Xunit;

namespace SharedWorlds.GameAdapters.Smalland.Tests;

public sealed class SmallandStockPakBoundaryTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"sharedworlds-smalland-stock-paks-{Guid.NewGuid():N}");

    [Fact]
    public void CurrentSteamStockPakSetIsAccepted()
    {
        var installation = CreateInstallation();
        var paksRoot = installation.Metadata![SmallandInstallationDiscovery.PaksRootPathKey];
        Directory.CreateDirectory(paksRoot);

        for (var chunk = 0; chunk <= 5; chunk++)
        {
            File.WriteAllBytes(
                Path.Combine(paksRoot, $"pakchunk{chunk}-WindowsNoEditor.pak"),
                [1]);
        }

        SmallandEnvironment.RequireVanillaInstallation(installation);
    }

    [Theory]
    [InlineData("pakchunk6-WindowsNoEditor.pak")]
    [InlineData("pakchunk999-WindowsNoEditor.pak")]
    public void UnknownStockLikePakIsRejected(string fileName)
    {
        var installation = CreateInstallation();
        var paksRoot = installation.Metadata![SmallandInstallationDiscovery.PaksRootPathKey];
        Directory.CreateDirectory(paksRoot);
        File.WriteAllBytes(Path.Combine(paksRoot, fileName), [1]);

        var exception = Assert.Throws<InvalidOperationException>(() =>
            SmallandEnvironment.RequireVanillaInstallation(installation));

        Assert.Contains("non-stock-named", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("vanilla-only", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    private GameInstallation CreateInstallation()
    {
        var installRoot = Path.Combine(_root, $"install-{Guid.NewGuid():N}");
        var paksRoot = Path.Combine(installRoot, "SMALLAND", "Content", "Paks");
        Directory.CreateDirectory(installRoot);

        return new GameInstallation(
            $"smalland:test:{Guid.NewGuid():N}",
            installRoot,
            "test",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [SmallandInstallationDiscovery.PaksRootPathKey] = paksRoot
            });
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
        catch
        {
            // Test cleanup is best-effort.
        }
    }
}
