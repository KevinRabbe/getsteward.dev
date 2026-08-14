using SharedWorlds.Core.Storage;

namespace SharedWorlds.Core.Tests;

public sealed class DisposableStatePackageStorageTests
{
    [Fact]
    public void CreatePackagePathUsesOnlyProcessLocalStaging()
    {
        var adapterId = $"test-{Guid.NewGuid():N}";
        var root = DisposableStatePackageStorage.GetAdapterRoot(adapterId);

        try
        {
            var package = DisposableStatePackageStorage.CreatePackagePath(
                adapterId,
                "my/world",
                ".zip");

            Assert.StartsWith(
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar,
                Path.GetFullPath(package),
                PathComparison);
            Assert.Equal(".zip", Path.GetExtension(package));
            Assert.DoesNotContain("my/world", package, StringComparison.Ordinal);
            Assert.True(Directory.Exists(root));
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public void CreatePackagePathAllocatesUniqueNames()
    {
        var adapterId = $"test-{Guid.NewGuid():N}";
        var root = DisposableStatePackageStorage.GetAdapterRoot(adapterId);

        try
        {
            var first = DisposableStatePackageStorage.CreatePackagePath(adapterId, "world", "sav");
            var second = DisposableStatePackageStorage.CreatePackagePath(adapterId, "world", "sav");

            Assert.NotEqual(first, second);
            Assert.Equal(".sav", Path.GetExtension(first));
            Assert.Equal(".sav", Path.GetExtension(second));
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData(".")]
    [InlineData("..")]
    public void AdapterIdMustBeOneSafePathSegment(string adapterId)
    {
        Assert.Throws<ArgumentException>(() =>
            DisposableStatePackageStorage.CreatePackagePath(adapterId, "world", ".zip"));
    }

    [Theory]
    [InlineData(".tar.gz")]
    [InlineData("../zip")]
    [InlineData("/zip")]
    public void ExtensionMustBeOneSafeExtension(string extension)
    {
        var adapterId = $"test-{Guid.NewGuid():N}";
        Assert.Throws<ArgumentException>(() =>
            DisposableStatePackageStorage.CreatePackagePath(adapterId, "world", extension));
    }

    private static StringComparison PathComparison => OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;

    private static void TryDelete(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
            // Test cleanup only.
        }
    }
}
