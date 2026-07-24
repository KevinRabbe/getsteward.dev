namespace SharedWorlds.GameAdapters.ProjectZomboid.Tests;

public sealed class ProjectZomboidWorkshopMetadataTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"sharedworlds-pz-workshop-metadata-{Guid.NewGuid():N}");

    [Fact]
    public void ReadWorkshopModIdsAcceptsOrdinaryNestedMetadata()
    {
        var contentRoot = Path.Combine(_root, "ordinary");
        WriteModInfo(Path.Combine(contentRoot, "mods", "Alpha", "42", "mod.info"), "alpha");
        WriteModInfo(Path.Combine(contentRoot, "mods", "Beta", "common", "mod.info"), "beta");

        var ids = ProjectZomboidEnvironment.ReadWorkshopModIds(contentRoot, "1234567890");

        Assert.Equal(["alpha", "beta"], ids.OrderBy(id => id, StringComparer.Ordinal));
    }

    [Fact]
    public void ReadWorkshopModIdsRejectsLinkedDirectory()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var contentRoot = Path.Combine(_root, "linked-directory");
        var outside = Path.Combine(_root, "outside-directory");
        Directory.CreateDirectory(contentRoot);
        Directory.CreateDirectory(outside);
        WriteModInfo(Path.Combine(outside, "mod.info"), "outside");

        var linkedDirectory = Path.Combine(contentRoot, "linked");
        Directory.CreateSymbolicLink(linkedDirectory, outside);
        try
        {
            var exception = Assert.Throws<InvalidOperationException>(() =>
                ProjectZomboidEnvironment.ReadWorkshopModIds(contentRoot, "111"));

            Assert.Contains("linked or reparse-point", exception.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(linkedDirectory);
        }
    }

    [Fact]
    public void ReadWorkshopModIdsRejectsLinkedModInfoFile()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var contentRoot = Path.Combine(_root, "linked-file");
        var metadataRoot = Path.Combine(contentRoot, "mods", "Example");
        Directory.CreateDirectory(metadataRoot);
        var outside = Path.Combine(_root, "outside-mod.info");
        File.WriteAllText(outside, "id=outside");

        var linkedFile = Path.Combine(metadataRoot, "mod.info");
        File.CreateSymbolicLink(linkedFile, outside);
        try
        {
            var exception = Assert.Throws<InvalidOperationException>(() =>
                ProjectZomboidEnvironment.ReadWorkshopModIds(contentRoot, "222"));

            Assert.Contains("linked or reparse-point", exception.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            File.Delete(linkedFile);
        }
    }

    [Fact]
    public void ReadWorkshopModIdsStopsAfterBoundedMetadataCount()
    {
        var contentRoot = Path.Combine(_root, "bounded");
        for (var index = 0; index < 513; index++)
        {
            WriteModInfo(
                Path.Combine(contentRoot, "mods", index.ToString(), "mod.info"),
                $"mod-{index}");
        }

        var exception = Assert.Throws<InvalidOperationException>(() =>
            ProjectZomboidEnvironment.ReadWorkshopModIds(contentRoot, "333"));

        Assert.Contains("more than 512 mod.info files", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static void WriteModInfo(string path, string id)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, $"id={id}");
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
