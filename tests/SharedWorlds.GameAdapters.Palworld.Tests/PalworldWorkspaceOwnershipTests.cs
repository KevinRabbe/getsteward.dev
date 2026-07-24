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
