using SharedWorlds.Core.Abstractions;
using SharedWorlds.Core.Environment;

namespace SharedWorlds.GameAdapters.Palworld.Tests;

public sealed class PalworldWorkspaceOwnershipTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"sharedworlds-palworld-ownership-{Guid.NewGuid():N}");

    [Fact]
    public void ExactDedicatedWorldPathIsAccepted()
    {
        var serverRoot = Path.Combine(_root, "server");
        var worldId = "0123456789ABCDEF";
        var expected = Path.Combine(
            serverRoot,
            "Pal",
            "Saved",
            "SaveGames",
            "0",
            worldId);

        PalworldWorkspaceOwnership.RequireOwned(Prepared(serverRoot, expected, worldId));
    }

    [Fact]
    public async Task AdapterRefusesJournalPathThatDoesNotMatchDedicatedWorldIdentity()
    {
        var serverRoot = Path.Combine(_root, "server");
        var worldId = "0123456789ABCDEF";
        var arbitrary = Path.Combine(_root, "unowned", worldId);
        Directory.CreateDirectory(arbitrary);
        await File.WriteAllBytesAsync(Path.Combine(arbitrary, "Level.sav"), [1, 2, 3]);
        var prepared = Prepared(serverRoot, arbitrary, worldId);
        var adapter = new PalworldAdapter();

        var capture = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            adapter.CaptureStateAsync(prepared));
        Assert.Contains("unrecognized Palworld dedicated World path", capture.Message, StringComparison.OrdinalIgnoreCase);

        var restore = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            adapter.RestoreStateAsync(
                prepared,
                new StatePackage("missing", Path.Combine(_root, "missing.zip"))));
        Assert.Contains("unrecognized Palworld dedicated World path", restore.Message, StringComparison.OrdinalIgnoreCase);

        var launch = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            adapter.LaunchHostAsync(prepared));
        Assert.Contains("unrecognized Palworld dedicated World path", launch.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AdapterRefusesLinkedSaveGamesAncestorBeforeStateAccess()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var serverRoot = Path.Combine(_root, "linked-server");
        var savedRoot = Path.Combine(serverRoot, "Pal", "Saved");
        Directory.CreateDirectory(savedRoot);

        var worldId = "FEDCBA9876543210";
        var outsideSaveGames = Path.Combine(_root, "outside-save-games");
        var outsideWorld = Path.Combine(outsideSaveGames, "0", worldId);
        Directory.CreateDirectory(outsideWorld);
        var outsideLevel = Path.Combine(outsideWorld, "Level.sav");
        await File.WriteAllBytesAsync(outsideLevel, [7, 8, 9]);

        var linkedSaveGames = Path.Combine(savedRoot, "SaveGames");
        Directory.CreateSymbolicLink(linkedSaveGames, outsideSaveGames);
        var workingDirectory = Path.Combine(linkedSaveGames, "0", worldId);
        var prepared = Prepared(serverRoot, workingDirectory, worldId);
        var adapter = new PalworldAdapter();

        try
        {
            var capture = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                adapter.CaptureStateAsync(prepared));
            Assert.Contains("linked/reparse", capture.Message, StringComparison.OrdinalIgnoreCase);

            var restore = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                adapter.RestoreStateAsync(
                    prepared,
                    new StatePackage("missing", Path.Combine(_root, "missing-linked.zip"))));
            Assert.Contains("linked/reparse", restore.Message, StringComparison.OrdinalIgnoreCase);

            Assert.Equal([7, 8, 9], await File.ReadAllBytesAsync(outsideLevel));
        }
        finally
        {
            Directory.Delete(linkedSaveGames);
        }
    }

    [Theory]
    [InlineData("../outside")]
    [InlineData("..")]
    [InlineData(".")]
    public void UnsafeJournaledWorldIdIsRejected(string worldId)
    {
        var serverRoot = Path.Combine(_root, "unsafe-server");
        var workingDirectory = Path.Combine(_root, "unsafe-working");

        var exception = Assert.Throws<InvalidOperationException>(() =>
            PalworldWorkspaceOwnership.RequireOwned(
                Prepared(serverRoot, workingDirectory, worldId)));

        Assert.Contains("unrecognized Palworld dedicated World path", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static PreparedWorld Prepared(
        string serverRoot,
        string workingDirectory,
        string worldId)
        => new(
            new GameInstallation(
                "palworld:test",
                Path.GetTempPath(),
                "test",
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [PalworldInstallationDiscovery.DedicatedServerRootPathKey] = serverRoot
                }),
            workingDirectory,
            new EnvironmentManifest(
                1,
                "palworld",
                "test-build",
                [],
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["hostingMode"] = "dedicated-server",
                    ["dedicatedServerName"] = worldId
                }));

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
