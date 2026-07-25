using System.IO.Compression;

namespace SharedWorlds.GameAdapters.Factorio.Tests;

public sealed class FactorioModCatalogLinkedPathTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"sharedworlds-factorio-linked-mod-{Guid.NewGuid():N}");

    [Fact]
    public void DiscoverRejectsDirectoryModContainingLinkedContent()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var modsRoot = Path.Combine(_root, "mods");
        var modRoot = Path.Combine(modsRoot, "linked_mod_1.0.0");
        var outside = Path.Combine(_root, "outside-mod-content");
        Directory.CreateDirectory(modRoot);
        Directory.CreateDirectory(outside);
        File.WriteAllText(
            Path.Combine(modRoot, "info.json"),
            "{\"name\":\"linked_mod\",\"version\":\"1.0.0\"}");
        File.WriteAllText(Path.Combine(outside, "control.lua"), "return true");

        var linkedDirectory = Path.Combine(modRoot, "linked-content");
        Directory.CreateSymbolicLink(linkedDirectory, outside);
        try
        {
            var artifacts = FactorioModCatalog.Discover(modsRoot);

            Assert.DoesNotContain(
                artifacts,
                artifact => string.Equals(artifact.Name, "linked_mod", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            Directory.Delete(linkedDirectory);
        }
    }

    [Fact]
    public void DiscoverRejectsLinkedZipMod()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var modsRoot = Path.Combine(_root, "mods-zip");
        Directory.CreateDirectory(modsRoot);
        var outsideArchive = Path.Combine(_root, "outside_mod_2.0.0.zip");
        using (var archive = ZipFile.Open(outsideArchive, ZipArchiveMode.Create))
        {
            var info = archive.CreateEntry("info.json");
            using var writer = new StreamWriter(info.Open());
            writer.Write("{\"name\":\"outside_mod\",\"version\":\"2.0.0\"}");
        }

        var linkedArchive = Path.Combine(modsRoot, "outside_mod_2.0.0.zip");
        File.CreateSymbolicLink(linkedArchive, outsideArchive);
        try
        {
            var artifacts = FactorioModCatalog.Discover(modsRoot);

            Assert.DoesNotContain(
                artifacts,
                artifact => string.Equals(artifact.Name, "outside_mod", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            File.Delete(linkedArchive);
        }
    }

    [Fact]
    public void DiscoverStillAcceptsOrdinaryDirectoryMod()
    {
        var modsRoot = Path.Combine(_root, "ordinary-mods");
        var modRoot = Path.Combine(modsRoot, "ordinary_mod_3.0.0");
        Directory.CreateDirectory(modRoot);
        File.WriteAllText(
            Path.Combine(modRoot, "info.json"),
            "{\"name\":\"ordinary_mod\",\"version\":\"3.0.0\"}");
        File.WriteAllText(Path.Combine(modRoot, "control.lua"), "return true");

        var artifact = Assert.Single(FactorioModCatalog.Discover(modsRoot));

        Assert.Equal("ordinary_mod", artifact.Name);
        Assert.Equal("3.0.0", artifact.Version);
        Assert.True(artifact.IsDirectory);
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
