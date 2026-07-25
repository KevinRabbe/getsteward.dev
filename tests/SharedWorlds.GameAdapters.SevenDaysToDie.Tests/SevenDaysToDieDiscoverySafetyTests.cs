using SharedWorlds.Core.Abstractions;

namespace SharedWorlds.GameAdapters.SevenDaysToDie.Tests;

public sealed class SevenDaysToDieDiscoverySafetyTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"sharedworlds-7dtd-discovery-safety-{Guid.NewGuid():N}");

    [Fact]
    public void DiscoverSkipsLinkedUserDataRoot()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var realUserData = Path.Combine(_root, "real-user-data");
        _ = CreateWorld(realUserData, "Navezgane", "linked-root-world");
        var linkedUserData = Path.Combine(_root, "linked-user-data");
        Directory.CreateSymbolicLink(linkedUserData, realUserData);
        try
        {
            var worlds = SevenDaysToDieWorldDiscovery.Discover(CreateInstallation(linkedUserData));

            Assert.Empty(worlds);
        }
        finally
        {
            Directory.Delete(linkedUserData);
        }
    }

    [Fact]
    public void DiscoverSkipsLinkedWorldDirectory()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var userData = Path.Combine(_root, "user-data-world-link");
        var savesRoot = Path.Combine(userData, "Saves");
        Directory.CreateDirectory(savesRoot);
        var outsideWorld = Path.Combine(_root, "outside-world-group");
        _ = CreateSaveUnderWorld(outsideWorld, "linked-world-save");
        var linkedWorld = Path.Combine(savesRoot, "LinkedWorld");
        Directory.CreateSymbolicLink(linkedWorld, outsideWorld);
        try
        {
            var worlds = SevenDaysToDieWorldDiscovery.Discover(CreateInstallation(userData));

            Assert.DoesNotContain(worlds, world =>
                world.DisplayName.Contains("linked-world-save", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            Directory.Delete(linkedWorld);
        }
    }

    [Fact]
    public void DiscoverSkipsLinkedSaveDirectory()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var userData = Path.Combine(_root, "user-data-save-link");
        var worldRoot = Path.Combine(userData, "Saves", "Navezgane");
        Directory.CreateDirectory(worldRoot);
        var outsideSave = Path.Combine(_root, "outside-save");
        Directory.CreateDirectory(outsideSave);
        File.WriteAllBytes(Path.Combine(outsideSave, "main.ttw"), [1, 2, 3]);
        var linkedSave = Path.Combine(worldRoot, "LinkedSave");
        Directory.CreateSymbolicLink(linkedSave, outsideSave);
        try
        {
            var worlds = SevenDaysToDieWorldDiscovery.Discover(CreateInstallation(userData));

            Assert.DoesNotContain(worlds, world =>
                string.Equals(
                    world.SourcePath,
                    Path.GetFullPath(linkedSave),
                    StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            Directory.Delete(linkedSave);
        }
    }

    [Fact]
    public void DiscoverSkipsSaveWhoseMainMarkerIsLinked()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var userData = Path.Combine(_root, "user-data-marker-link");
        var save = Path.Combine(userData, "Saves", "Navezgane", "LinkedMarker");
        Directory.CreateDirectory(save);
        var outsideMarker = Path.Combine(_root, "outside-main.ttw");
        File.WriteAllBytes(outsideMarker, [4, 5, 6]);
        var linkedMarker = Path.Combine(save, "main.ttw");
        File.CreateSymbolicLink(linkedMarker, outsideMarker);
        try
        {
            var worlds = SevenDaysToDieWorldDiscovery.Discover(CreateInstallation(userData));

            Assert.DoesNotContain(worlds, world =>
                string.Equals(
                    world.SourcePath,
                    Path.GetFullPath(save),
                    StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            File.Delete(linkedMarker);
        }
    }

    private static string CreateWorld(
        string userData,
        string worldName,
        string saveName)
    {
        var worldRoot = Path.Combine(userData, "Saves", worldName);
        Directory.CreateDirectory(worldRoot);
        return CreateSaveUnderWorld(worldRoot, saveName);
    }

    private static string CreateSaveUnderWorld(string worldRoot, string saveName)
    {
        var save = Path.Combine(worldRoot, saveName);
        Directory.CreateDirectory(save);
        File.WriteAllBytes(Path.Combine(save, "main.ttw"), [1, 2, 3]);
        return save;
    }

    private static GameInstallation CreateInstallation(string userData)
        => new(
            "7-days-to-die:test",
            userData,
            "test",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [SevenDaysToDieInstallationDiscovery.UserDataPathKey] = userData
            });

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
