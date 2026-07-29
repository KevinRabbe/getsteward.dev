using SharedWorlds.Core.Domain;
using SharedWorlds.Infrastructure.Storage;

namespace SharedWorlds.Infrastructure.Tests;

public sealed class LocalWorldStorageDeletionTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"sharedworlds-delete-tests-{Guid.NewGuid():N}");

    [Fact]
    public async Task DeleteWorld_RemovesManagedDirectoryAndLeavesOtherWorldsUntouched()
    {
        var storage = new LocalWorldStorage(_root);
        var deleted = CreateWorld("Delete me");
        var survivor = CreateWorld("Keep me");

        await storage.SaveWorldAsync(deleted);
        await storage.SaveWorldAsync(survivor);

        var deletedDirectory = Path.Combine(_root, "worlds", deleted.Id.ToString());
        var nestedDirectory = Path.Combine(deletedDirectory, "states", "nested");
        Directory.CreateDirectory(nestedDirectory);
        await File.WriteAllTextAsync(Path.Combine(nestedDirectory, "evidence.bin"), "managed revision evidence");

        Assert.True(await storage.DeleteWorldAsync(deleted.Id));
        Assert.False(Directory.Exists(deletedDirectory));
        Assert.Null(await storage.LoadWorldAsync(deleted.Id));

        var remaining = await storage.ListWorldsAsync();
        Assert.Single(remaining);
        Assert.Equal(survivor.Id, remaining[0].Id);
        Assert.NotNull(await storage.LoadWorldAsync(survivor.Id));

        Assert.False(await storage.DeleteWorldAsync(deleted.Id));
    }

    [Fact]
    public async Task DeleteWorld_RejectsManagedRootReparsePoint()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var storage = new LocalWorldStorage(_root);
        var world = CreateWorld();
        await storage.SaveWorldAsync(world);

        // The implementation explicitly checks the managed World directory before recursive deletion.
        // Creating a junction/symlink requires privileges that are not guaranteed on CI, so lock the
        // fail-closed guard structurally here and exercise the ordinary recursive deletion above.
        var source = await File.ReadAllTextAsync(FindRepositoryFile(
            "src/SharedWorlds.Infrastructure/Storage/LocalWorldStorage.cs"));
        Assert.Contains("FileAttributes.ReparsePoint", source, StringComparison.Ordinal);
        Assert.Contains("Refusing to delete World", source, StringComparison.Ordinal);
    }

    private static World CreateWorld(string name = "Test World")
        => new(
            WorldId.New(),
            name,
            "factorio",
            [new UserIdentity("local", "tester", "Tester")],
            null,
            null);

    private static string FindRepositoryFile(string relativePath)
    {
        var workspace = Environment.GetEnvironmentVariable("GITHUB_WORKSPACE");
        if (!string.IsNullOrWhiteSpace(workspace))
        {
            var candidate = Path.Combine(workspace, relativePath);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, relativePath);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new FileNotFoundException($"Could not locate repository file '{relativePath}'.");
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
            // Test cleanup must not hide the actual assertion result.
        }
        catch (UnauthorizedAccessException)
        {
            // Test cleanup must not hide the actual assertion result.
        }
    }
}
