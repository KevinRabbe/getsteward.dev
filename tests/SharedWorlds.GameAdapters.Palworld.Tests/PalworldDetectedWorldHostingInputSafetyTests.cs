using SharedWorlds.Core.Abstractions;

namespace SharedWorlds.GameAdapters.Palworld.Tests;

public sealed class PalworldDetectedWorldHostingInputSafetyTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"sharedworlds-palworld-host-copy-{Guid.NewGuid():N}");

    [Fact]
    public async Task PreparationRejectsLinkedDirectoryBeforeCopyingExternalBytes()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var (installation, sourceWorldPath, worldId) = await CreateInputsAsync();
        var outside = Path.Combine(_root, "outside-directory");
        Directory.CreateDirectory(outside);
        await File.WriteAllBytesAsync(Path.Combine(outside, "external.sav"), [9, 8, 7, 6]);
        Directory.CreateSymbolicLink(Path.Combine(sourceWorldPath, "Players"), outside);

        var adapter = new PalworldAdapter();
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            adapter.PrepareDetectedWorldForHostingAsync(
                installation,
                new DetectedWorld(worldId, "Linked directory World", sourceWorldPath)));

        Assert.Contains("linked or a reparse point", exception.Message, StringComparison.OrdinalIgnoreCase);
        AssertDestinationWasNotPublished(installation, worldId);
    }

    [Fact]
    public async Task PreparationRejectsLinkedFileBeforeCopyingExternalBytes()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var (installation, sourceWorldPath, worldId) = await CreateInputsAsync();
        var outside = Path.Combine(_root, "outside-file.sav");
        await File.WriteAllBytesAsync(outside, [4, 3, 2, 1]);
        File.CreateSymbolicLink(Path.Combine(sourceWorldPath, "External.sav"), outside);

        var adapter = new PalworldAdapter();
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            adapter.PrepareDetectedWorldForHostingAsync(
                installation,
                new DetectedWorld(worldId, "Linked file World", sourceWorldPath)));

        Assert.Contains("linked or a reparse point", exception.Message, StringComparison.OrdinalIgnoreCase);
        AssertDestinationWasNotPublished(installation, worldId);
    }

    private async Task<(GameInstallation Installation, string SourceWorldPath, string WorldId)> CreateInputsAsync()
    {
        var serverRoot = Path.Combine(_root, "server", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(serverRoot);
        var executable = Path.Combine(serverRoot, "PalServer.exe");
        await File.WriteAllBytesAsync(executable, [0x4D, 0x5A]);

        var worldId = Guid.NewGuid().ToString("N").ToUpperInvariant();
        var sourceWorldPath = Path.Combine(_root, "source", worldId);
        Directory.CreateDirectory(sourceWorldPath);
        await File.WriteAllBytesAsync(Path.Combine(sourceWorldPath, "Level.sav"), [1, 2, 3]);

        var installation = new GameInstallation(
            "palworld:test",
            Path.Combine(_root, "client"),
            "test",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [PalworldInstallationDiscovery.DedicatedServerRootPathKey] = serverRoot,
                [PalworldInstallationDiscovery.DedicatedServerExecutablePathKey] = executable
            });

        return (installation, sourceWorldPath, worldId);
    }

    private static void AssertDestinationWasNotPublished(
        GameInstallation installation,
        string worldId)
    {
        var serverRoot = installation.Metadata![PalworldInstallationDiscovery.DedicatedServerRootPathKey];
        var profileRoot = Path.Combine(serverRoot, "Pal", "Saved", "SaveGames", "0");
        var destination = Path.Combine(profileRoot, worldId);

        Assert.False(Directory.Exists(destination));
        if (Directory.Exists(profileRoot))
        {
            Assert.Empty(Directory.EnumerateDirectories(
                profileRoot,
                worldId + ".sharedworlds-staging-*",
                SearchOption.TopDirectoryOnly));
        }
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
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
