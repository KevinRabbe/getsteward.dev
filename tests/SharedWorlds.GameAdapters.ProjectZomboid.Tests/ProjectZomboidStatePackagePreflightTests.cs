using System.IO.Compression;

namespace SharedWorlds.GameAdapters.ProjectZomboid.Tests;

public sealed class ProjectZomboidStatePackagePreflightTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"sharedworlds-pz-preflight-{Guid.NewGuid():N}");

    [Fact]
    public void DeclaredExtractionBytesSumOnlyFileEntries()
    {
        var package = CreateArchive(
            ("empty/", Array.Empty<byte>()),
            ("empty/a.bin", new byte[] { 1, 2, 3 }),
            ("b.bin", new byte[] { 4, 5, 6, 7 }));

        using var archive = ZipFile.OpenRead(package);
        var bytes = ProjectZomboidStatePackagePreflight.GetDeclaredExtractionBytes(
            archive,
            maxEntries: 10);

        Assert.Equal(7, bytes);
    }

    [Fact]
    public void EntryCountLimitStopsBeforeExtraction()
    {
        var package = CreateArchive(
            ("a.bin", Array.Empty<byte>()),
            ("b.bin", Array.Empty<byte>()),
            ("c.bin", Array.Empty<byte>()));

        using var archive = ZipFile.OpenRead(package);
        var exception = Assert.Throws<InvalidDataException>(() =>
            ProjectZomboidStatePackagePreflight.GetDeclaredExtractionBytes(
                archive,
                maxEntries: 2));

        Assert.Contains("more than 2 archive entries", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void FreeSpaceReserveFailsClosedWhenDeclaredExtractionCannotFit()
    {
        var exception = Assert.Throws<IOException>(() =>
            ProjectZomboidStatePackagePreflight.EnsureSufficientFreeSpace(
                Path.Combine(_root, "workspace"),
                declaredBytes: 100,
                reserveBytes: 20,
                availableBytesOverride: 119));

        Assert.Contains("requires 120 bytes", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void FreeSpaceReserveAcceptsExactRequiredCapacity()
    {
        ProjectZomboidStatePackagePreflight.EnsureSufficientFreeSpace(
            Path.Combine(_root, "workspace"),
            declaredBytes: 100,
            reserveBytes: 20,
            availableBytesOverride: 120);
    }

    private string CreateArchive(params (string Name, byte[] Bytes)[] entries)
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, $"{Guid.NewGuid():N}.zip");
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (name, bytes) in entries)
        {
            var entry = archive.CreateEntry(name);
            if (name.EndsWith('/'))
            {
                continue;
            }

            using var stream = entry.Open();
            stream.Write(bytes);
        }

        return path;
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
