using System.IO.Compression;
using SharedWorlds.Core.Abstractions;
using SharedWorlds.Core.Environment;

namespace SharedWorlds.GameAdapters.SevenDaysToDie.Tests;

public sealed class SevenDaysToDieWorldStateTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"sharedworlds-7dtd-state-{Guid.NewGuid():N}");
    private readonly List<string> _packages = [];

    [Fact]
    public async Task CaptureAndRestorePreserveDirectoryWorldBytes()
    {
        var source = Path.Combine(_root, "source");
        Directory.CreateDirectory(Path.Combine(source, "Player"));
        Directory.CreateDirectory(Path.Combine(source, "Region"));
        await File.WriteAllBytesAsync(Path.Combine(source, "main.ttw"), [1, 2, 3, 4]);
        await File.WriteAllBytesAsync(Path.Combine(source, "Player", "player.ttp"), [5, 6, 7]);
        await File.WriteAllBytesAsync(Path.Combine(source, "Region", "r.0.0.7rg"), [8, 9]);

        var detected = new DetectedWorld(source, "Test", source);
        var captured = await SevenDaysToDieWorldState.CaptureDetectedWorldAsync(
            detected,
            CancellationToken.None);
        _packages.Add(captured.Package.Path);

        using (var archive = ZipFile.OpenRead(captured.Package.Path))
        {
            Assert.Contains(archive.Entries, entry => entry.FullName == "main.ttw");
            Assert.Contains(archive.Entries, entry => entry.FullName == "Player/player.ttp");
            Assert.Contains(archive.Entries, entry => entry.FullName == "Region/r.0.0.7rg");
        }

        var destination = Path.Combine(_root, "prepared", "world");
        var prepared = new PreparedWorld(
            new GameInstallation("7-days-to-die:test", _root, "test"),
            destination,
            EmptyEnvironment());
        await SevenDaysToDieWorldState.RestorePreparedWorldAsync(
            prepared,
            captured.Package,
            CancellationToken.None);

        Assert.Equal(
            await File.ReadAllBytesAsync(Path.Combine(source, "main.ttw")),
            await File.ReadAllBytesAsync(Path.Combine(destination, "main.ttw")));
        Assert.Equal(
            await File.ReadAllBytesAsync(Path.Combine(source, "Player", "player.ttp")),
            await File.ReadAllBytesAsync(Path.Combine(destination, "Player", "player.ttp")));
        Assert.Equal(
            await File.ReadAllBytesAsync(Path.Combine(source, "Region", "r.0.0.7rg")),
            await File.ReadAllBytesAsync(Path.Combine(destination, "Region", "r.0.0.7rg")));
    }

    [Fact]
    public async Task RestoreRejectsPathTraversalAndLeavesPreparedWorldUntouched()
    {
        var package = Path.Combine(_root, "malicious.zip");
        Directory.CreateDirectory(_root);
        using (var archive = ZipFile.Open(package, ZipArchiveMode.Create))
        {
            var main = archive.CreateEntry("main.ttw");
            await using (var stream = main.Open())
            {
                await stream.WriteAsync(new byte[] { 1 });
            }

            var escape = archive.CreateEntry("../escaped.txt");
            await using (var stream = escape.Open())
            {
                await stream.WriteAsync(new byte[] { 2 });
            }
        }

        var destination = Path.Combine(_root, "prepared", "world");
        Directory.CreateDirectory(destination);
        await File.WriteAllTextAsync(Path.Combine(destination, "main.ttw"), "original");
        var prepared = new PreparedWorld(
            new GameInstallation("7-days-to-die:test", _root, "test"),
            destination,
            EmptyEnvironment());

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            SevenDaysToDieWorldState.RestorePreparedWorldAsync(
                prepared,
                new StatePackage("malicious", package),
                CancellationToken.None));

        Assert.Equal("original", await File.ReadAllTextAsync(Path.Combine(destination, "main.ttw")));
        Assert.False(File.Exists(Path.Combine(_root, "prepared", "escaped.txt")));
    }

    [Fact]
    public async Task RestoreWithoutMainTtwFailsBeforeReplacingExistingWorld()
    {
        var package = Path.Combine(_root, "incomplete.zip");
        Directory.CreateDirectory(_root);
        using (var archive = ZipFile.Open(package, ZipArchiveMode.Create))
        {
            var entry = archive.CreateEntry("Region/r.0.0.7rg");
            await using var stream = entry.Open();
            await stream.WriteAsync(new byte[] { 4, 5, 6 });
        }

        var destination = Path.Combine(_root, "prepared", "world");
        Directory.CreateDirectory(destination);
        await File.WriteAllTextAsync(Path.Combine(destination, "main.ttw"), "original");
        var prepared = new PreparedWorld(
            new GameInstallation("7-days-to-die:test", _root, "test"),
            destination,
            EmptyEnvironment());

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            SevenDaysToDieWorldState.RestorePreparedWorldAsync(
                prepared,
                new StatePackage("incomplete", package),
                CancellationToken.None));

        Assert.Equal("original", await File.ReadAllTextAsync(Path.Combine(destination, "main.ttw")));
    }

    [Fact]
    public async Task FinalizePreservesRecoveryWorkspaceButDeletesDiscardedWorkspace()
    {
        var preserveRoot = Path.Combine(_root, "preserve");
        var preserveWorld = Path.Combine(preserveRoot, "world");
        Directory.CreateDirectory(preserveWorld);
        var preparedPreserve = new PreparedWorld(
            new GameInstallation("7-days-to-die:test", _root, "test"),
            preserveWorld,
            EmptyEnvironment());

        await SevenDaysToDieWorldState.FinalizePreparedWorldAsync(
            preparedPreserve,
            PreparedWorldDisposition.PreserveForRecovery,
            CancellationToken.None);
        Assert.True(Directory.Exists(preserveRoot));

        var discardRoot = Path.Combine(_root, "discard");
        var discardWorld = Path.Combine(discardRoot, "world");
        Directory.CreateDirectory(discardWorld);
        var preparedDiscard = preparedPreserve with { WorkingDirectory = discardWorld };

        await SevenDaysToDieWorldState.FinalizePreparedWorldAsync(
            preparedDiscard,
            PreparedWorldDisposition.Discard,
            CancellationToken.None);
        Assert.False(Directory.Exists(discardRoot));
    }

    private static EnvironmentManifest EmptyEnvironment()
        => new(
            1,
            "7-days-to-die",
            "test-build",
            [],
            new Dictionary<string, string>(StringComparer.Ordinal));

    public void Dispose()
    {
        foreach (var package in _packages)
        {
            try
            {
                if (File.Exists(package))
                {
                    File.Delete(package);
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

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
