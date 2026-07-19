using SharedWorlds.Core.Abstractions;
using SharedWorlds.GameAdapters.Factorio;

namespace SharedWorlds.GameAdapters.Factorio.Tests;

public sealed class FactorioSaveDiscoveryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"sharedworlds-factorio-tests-{Guid.NewGuid():N}");

    [Fact]
    public async Task DiscoverWorlds_ReturnsRegularSavesAndExcludesAutosaves()
    {
        var saves = Path.Combine(_root, "saves");
        Directory.CreateDirectory(saves);
        await File.WriteAllBytesAsync(Path.Combine(saves, "our-world.zip"), [1, 2, 3]);
        await File.WriteAllBytesAsync(Path.Combine(saves, "_autosave1.zip"), [4, 5, 6]);

        var installation = new GameInstallation(
            Id: "test-factorio",
            RootPath: _root,
            Source: "test",
            Metadata: new Dictionary<string, string>
            {
                ["userDataPath"] = _root,
                ["executablePath"] = Path.Combine(_root, "factorio.exe")
            });

        var adapter = new FactorioAdapter();
        var worlds = await adapter.DiscoverWorldsAsync(installation);

        var world = Assert.Single(worlds);
        Assert.Equal("our-world", world.DisplayName);
        Assert.EndsWith("our-world.zip", world.SourcePath, StringComparison.OrdinalIgnoreCase);
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
