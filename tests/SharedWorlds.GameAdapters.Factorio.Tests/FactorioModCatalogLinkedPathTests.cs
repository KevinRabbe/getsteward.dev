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
    public void DiscoverRejectsOversizedDirectoryInfoMetadata()
    {
        var modsRoot = Path.Combine(_root, "oversized-directory-metadata");
        var modRoot = Path.Combine(modsRoot, "oversized_mod_4.0.0");
        Directory.CreateDirectory(modRoot);
        var infoPath = Path.Combine(modRoot, "info.json");
        using (var stream = new FileStream(
                   infoPath,
                   FileMode.CreateNew,
                   FileAccess.Write,
                   FileShare.None))
        {
            stream.SetLength(FactorioModCatalog.MaximumInfoJsonBytes + 1L);
        }

        var artifacts = FactorioModCatalog.Discover(modsRoot);

        Assert.Empty(artifacts);
        Assert.Equal(
            FactorioModCatalog.MaximumInfoJsonBytes + 1L,
            new FileInfo(infoPath).Length);
    }

    [Fact]
    public void DiscoverRejectsOversizedZipInfoMetadata()
    {
        var modsRoot = Path.Combine(_root, "oversized-zip-metadata");
        Directory.CreateDirectory(modsRoot);
        var archivePath = Path.Combine(modsRoot, "oversized_zip_mod_5.0.0.zip");
        using (var archive = ZipFile.Open(archivePath, ZipArchiveMode.Create))
        {
            var info = archive.CreateEntry("oversized_zip_mod_5.0.0/info.json", CompressionLevel.Optimal);
            using var stream = info.Open();
            WriteBytes(stream, FactorioModCatalog.MaximumInfoJsonBytes + 1L);
        }

        var artifacts = FactorioModCatalog.Discover(modsRoot);

        Assert.Empty(artifacts);
        using var opened = ZipFile.OpenRead(archivePath);
        Assert.Equal(
            FactorioModCatalog.MaximumInfoJsonBytes + 1L,
            Assert.Single(opened.Entries).Length);
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

    private static void WriteBytes(Stream stream, long count)
    {
        var buffer = new byte[16 * 1024];
        var remaining = count;
        while (remaining > 0)
        {
            var length = (int)Math.Min(buffer.Length, remaining);
            stream.Write(buffer, 0, length);
            remaining -= length;
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
