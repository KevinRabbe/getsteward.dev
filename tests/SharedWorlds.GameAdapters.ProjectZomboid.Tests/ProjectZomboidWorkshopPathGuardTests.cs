using SharedWorlds.Core.Abstractions;

namespace SharedWorlds.GameAdapters.ProjectZomboid.Tests;

public sealed class ProjectZomboidWorkshopPathGuardTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"sharedworlds-pz-workshop-guard-{Guid.NewGuid():N}");

    [Fact]
    public void ValidateWorkshopItemRejectsLinkedDirectory()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var contentRoot = Path.Combine(_root, "content", "108600");
        var itemRoot = Path.Combine(contentRoot, "1234567890");
        var outside = Path.Combine(_root, "outside");
        Directory.CreateDirectory(Path.Combine(itemRoot, "mods", "Example"));
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "mod.info"), "id=outside");

        var linkedDirectory = Path.Combine(itemRoot, "mods", "Example", "linked");
        Directory.CreateSymbolicLink(linkedDirectory, outside);
        try
        {
            var exception = Assert.Throws<InvalidOperationException>(() =>
                ProjectZomboidWorkshopPathGuard.ValidateWorkshopItem(
                    contentRoot,
                    "1234567890"));

            Assert.Contains("linked or reparse-point", exception.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(linkedDirectory);
        }
    }

    [Fact]
    public void ConfiguredGuardDoesNotInspectUnreferencedWorkshopItem()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var contentRoot = Path.Combine(_root, "configured", "content", "108600");
        var referencedRoot = Path.Combine(contentRoot, "111");
        var unrelatedRoot = Path.Combine(contentRoot, "999");
        var outside = Path.Combine(_root, "configured", "outside");
        Directory.CreateDirectory(Path.Combine(referencedRoot, "mods", "Referenced"));
        Directory.CreateDirectory(unrelatedRoot);
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(referencedRoot, "mods", "Referenced", "mod.info"), "id=referenced");

        var unrelatedLink = Path.Combine(unrelatedRoot, "linked");
        Directory.CreateSymbolicLink(unrelatedLink, outside);

        var userDataRoot = Path.Combine(_root, "configured", "Zomboid");
        var worldPath = Path.Combine(userDataRoot, "Saves", "Multiplayer", "servertest");
        var serverRoot = Path.Combine(userDataRoot, "Server");
        Directory.CreateDirectory(worldPath);
        Directory.CreateDirectory(serverRoot);
        File.WriteAllText(
            Path.Combine(serverRoot, "servertest.ini"),
            "WorkshopItems=111\nMods=Referenced");

        try
        {
            ProjectZomboidWorkshopPathGuard.ValidateConfiguredWorkshopItems(
                Installation(userDataRoot, contentRoot),
                new DetectedWorld(worldPath, "servertest", worldPath));
        }
        finally
        {
            Directory.Delete(unrelatedLink);
        }
    }

    [Fact]
    public void ValidateWorkshopItemAcceptsOrdinaryContent()
    {
        var contentRoot = Path.Combine(_root, "ordinary", "content", "108600");
        var modRoot = Path.Combine(contentRoot, "222", "mods", "Example", "42");
        Directory.CreateDirectory(modRoot);
        File.WriteAllText(Path.Combine(modRoot, "mod.info"), "id=example");
        File.WriteAllText(Path.Combine(modRoot, "media.txt"), "ordinary");

        ProjectZomboidWorkshopPathGuard.ValidateWorkshopItem(contentRoot, "222");
    }

    private static GameInstallation Installation(string userDataRoot, string contentRoot)
        => new(
            "project-zomboid:test",
            Path.Combine(Path.GetTempPath(), "project-zomboid-test-install"),
            "test",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [ProjectZomboidInstallationDiscovery.UserDataPathKey] = userDataRoot,
                [ProjectZomboidInstallationDiscovery.WorkshopContentRootKey] = contentRoot
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
