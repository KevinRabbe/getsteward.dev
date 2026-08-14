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
    private readonly List<string> _ownedWorkspaces = [];

    [Fact]
    public async Task CaptureAndRestorePreserveSaveAndGeneratedWorldBytes()
    {
        var userData = Path.Combine(_root, "user-data");
        var source = Path.Combine(userData, "Saves", "StewardWorld", "StewardGame");
        var generated = Path.Combine(userData, "GeneratedWorlds", "StewardWorld");
        Directory.CreateDirectory(Path.Combine(source, "Player"));
        Directory.CreateDirectory(Path.Combine(source, "Region"));
        Directory.CreateDirectory(generated);
        await File.WriteAllBytesAsync(Path.Combine(source, "main.ttp"), [1, 2, 3, 4]);
        await File.WriteAllBytesAsync(Path.Combine(source, "Player", "player.ttp"), [5, 6, 7]);
        await File.WriteAllBytesAsync(Path.Combine(source, "Region", "r.0.0.7rg"), [8, 9]);
        await File.WriteAllBytesAsync(Path.Combine(generated, "biomes.png"), [10, 11]);
        await File.WriteAllBytesAsync(Path.Combine(generated, "dtm_processed.raw"), [12, 13]);

        var unrelated = Path.Combine(userData, "Saves", "OtherWorld", "OtherGame");
        Directory.CreateDirectory(unrelated);
        await File.WriteAllBytesAsync(Path.Combine(unrelated, "main.ttp"), [99]);

        var installation = Installation(userData);
        var detected = new DetectedWorld(source, "StewardGame (StewardWorld)", source);
        var captured = await SevenDaysToDieWorldState.CaptureDetectedWorldAsync(
            installation,
            detected,
            CancellationToken.None);
        _packages.Add(captured.Package.Path);

        using (var archive = ZipFile.OpenRead(captured.Package.Path))
        {
            var names = archive.Entries.Select(entry => entry.FullName).ToArray();
            Assert.Contains("Saves/StewardWorld/StewardGame/main.ttp", names);
            Assert.Contains("Saves/StewardWorld/StewardGame/Player/player.ttp", names);
            Assert.Contains("Saves/StewardWorld/StewardGame/Region/r.0.0.7rg", names);
            Assert.Contains("GeneratedWorlds/StewardWorld/biomes.png", names);
            Assert.Contains("GeneratedWorlds/StewardWorld/dtm_processed.raw", names);
            Assert.DoesNotContain(names, name => name.Contains("OtherWorld", StringComparison.OrdinalIgnoreCase));
        }

        var prepared = CreateOwnedPrepared(installation);
        var destination = prepared.WorkingDirectory;
        await SevenDaysToDieWorldState.RestorePreparedWorldAsync(
            prepared,
            captured.Package,
            CancellationToken.None);

        Assert.Equal(
            await File.ReadAllBytesAsync(Path.Combine(source, "main.ttp")),
            await File.ReadAllBytesAsync(Path.Combine(destination, "Saves", "StewardWorld", "StewardGame", "main.ttp")));
        Assert.Equal(
            await File.ReadAllBytesAsync(Path.Combine(generated, "biomes.png")),
            await File.ReadAllBytesAsync(Path.Combine(destination, "GeneratedWorlds", "StewardWorld", "biomes.png")));
    }

    [Fact]
    public async Task CaptureAcceptsLegacyMainTtwWithoutFormatRewrite()
    {
        var userData = Path.Combine(_root, "legacy-user-data");
        var source = Path.Combine(userData, "Saves", "Navezgane", "LegacyGame");
        Directory.CreateDirectory(source);
        await File.WriteAllBytesAsync(Path.Combine(source, "main.ttw"), [1, 2]);

        var captured = await SevenDaysToDieWorldState.CaptureDetectedWorldAsync(
            Installation(userData),
            new DetectedWorld(source, "LegacyGame (Navezgane)", source),
            CancellationToken.None);
        _packages.Add(captured.Package.Path);

        using var archive = ZipFile.OpenRead(captured.Package.Path);
        Assert.Contains(
            archive.Entries,
            entry => entry.FullName == "Saves/Navezgane/LegacyGame/main.ttw");
    }

    [Fact]
    public async Task CaptureRejectsLinkedDirectoryInsteadOfFollowingOutsideWorld()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var userData = Path.Combine(_root, "linked-user-data");
        var source = Path.Combine(userData, "Saves", "Navezgane", "LinkedGame");
        var outside = Path.Combine(_root, "outside-linked-world");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(outside);
        await File.WriteAllBytesAsync(Path.Combine(source, "main.ttp"), [1, 2]);
        await File.WriteAllBytesAsync(Path.Combine(outside, "outside.bin"), [9, 9, 9]);

        var linkedDirectory = Path.Combine(source, "LinkedOutside");
        Directory.CreateSymbolicLink(linkedDirectory, outside);
        try
        {
            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                SevenDaysToDieWorldState.CaptureDetectedWorldAsync(
                    Installation(userData),
                    new DetectedWorld(source, "LinkedGame (Navezgane)", source),
                    CancellationToken.None));

            Assert.Contains("linked or reparse-point", exception.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(linkedDirectory);
        }
    }

    [Fact]
    public async Task RestoreRejectsPathTraversalAndLeavesPreparedWorkspaceUntouched()
    {
        var package = Path.Combine(_root, "malicious.zip");
        Directory.CreateDirectory(_root);
        using (var archive = ZipFile.Open(package, ZipArchiveMode.Create))
        {
            await WriteEntryAsync(archive, "Saves/Navezgane/Test/main.ttp", [1]);
            await WriteEntryAsync(archive, "../escaped.txt", [2]);
        }

        var prepared = CreateOwnedPrepared(Installation(Path.Combine(_root, "unused")));
        var destination = prepared.WorkingDirectory;
        await File.WriteAllTextAsync(Path.Combine(destination, "sentinel.txt"), "original");

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            SevenDaysToDieWorldState.RestorePreparedWorldAsync(
                prepared,
                new StatePackage("malicious", package),
                CancellationToken.None));

        Assert.Equal("original", await File.ReadAllTextAsync(Path.Combine(destination, "sentinel.txt")));
        Assert.False(File.Exists(Path.Combine(Directory.GetParent(destination)!.FullName, "escaped.txt")));
        Assert.True(Directory.Exists(destination));
    }

    [Fact]
    public async Task RestoreRejectsGeneratedTerrainForDifferentWorld()
    {
        var package = Path.Combine(_root, "wrong-terrain.zip");
        Directory.CreateDirectory(_root);
        using (var archive = ZipFile.Open(package, ZipArchiveMode.Create))
        {
            await WriteEntryAsync(archive, "Saves/StewardWorld/StewardGame/main.ttp", [1]);
            await WriteEntryAsync(archive, "GeneratedWorlds/OtherWorld/biomes.png", [2]);
        }

        var prepared = CreateOwnedPrepared(Installation(Path.Combine(_root, "unused-2")));
        var destination = prepared.WorkingDirectory;
        await File.WriteAllTextAsync(Path.Combine(destination, "sentinel.txt"), "original");

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            SevenDaysToDieWorldState.RestorePreparedWorldAsync(
                prepared,
                new StatePackage("wrong-terrain", package),
                CancellationToken.None));

        Assert.Equal("original", await File.ReadAllTextAsync(Path.Combine(destination, "sentinel.txt")));
        Assert.True(Directory.Exists(destination));
    }

    [Fact]
    public async Task FinalizePreservesRecoveryWorkspaceButDeletesDiscardedWorkspace()
    {
        var preparedPreserve = CreateOwnedPrepared(Installation(Path.Combine(_root, "unused-3")));
        var preserveRoot = preparedPreserve.WorkingDirectory;

        await SevenDaysToDieWorldState.FinalizePreparedWorldAsync(
            preparedPreserve,
            PreparedWorldDisposition.PreserveForRecovery,
            CancellationToken.None);
        Assert.True(Directory.Exists(preserveRoot));

        var preparedDiscard = CreateOwnedPrepared(Installation(Path.Combine(_root, "unused-4")));
        var discardRoot = preparedDiscard.WorkingDirectory;

        await SevenDaysToDieWorldState.FinalizePreparedWorldAsync(
            preparedDiscard,
            PreparedWorldDisposition.Discard,
            CancellationToken.None);
        _ownedWorkspaces.Remove(discardRoot);
        Assert.False(Directory.Exists(discardRoot));
    }

    private PreparedWorld CreateOwnedPrepared(GameInstallation installation)
    {
        var workspace = SevenDaysToDieWorkspaceOwnership.Create();
        _ownedWorkspaces.Add(workspace);
        return new PreparedWorld(
            installation,
            workspace,
            EmptyEnvironment());
    }

    private static GameInstallation Installation(string userData)
        => new(
            "7-days-to-die:test",
            Path.GetDirectoryName(userData) ?? userData,
            "test",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [SevenDaysToDieInstallationDiscovery.UserDataPathKey] = userData
            });

    private static EnvironmentManifest EmptyEnvironment()
        => new(
            1,
            "7-days-to-die",
            "test-build",
            [],
            new Dictionary<string, string>(StringComparer.Ordinal));

    private static async Task WriteEntryAsync(ZipArchive archive, string name, byte[] bytes)
    {
        var entry = archive.CreateEntry(name);
        await using var stream = entry.Open();
        await stream.WriteAsync(bytes);
    }

    public void Dispose()
    {
        foreach (var workspace in _ownedWorkspaces.ToArray())
        {
            try
            {
                if (Directory.Exists(workspace))
                {
                    SevenDaysToDieWorkspaceOwnership.DeleteOwned(workspace);
                }
            }
            catch
            {
            }
        }

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