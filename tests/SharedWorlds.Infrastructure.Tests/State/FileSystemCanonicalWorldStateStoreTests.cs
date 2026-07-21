using SharedWorlds.Core.Abstractions;
using SharedWorlds.Core.State;
using SharedWorlds.Infrastructure.State;
using Xunit;

namespace SharedWorlds.Infrastructure.Tests.State;

public sealed class FileSystemCanonicalWorldStateStoreTests
{
    [Fact]
    public async Task CommitStoresPackageBeforeAdvancingHeadAndDeletesTemporarySource()
    {
        using var fixture = new TemporaryStoreFixture();
        var sourcePath = await fixture.CreatePackageAsync("first-state");
        var coordinator = fixture.CreateCoordinator();

        var result = await coordinator.CommitAsync(
            "world-1",
            expectedHeadRevisionId: null,
            CreateCapturedState(sourcePath, deleteAfterStore: true));
        var head = Assert.IsType<CanonicalWorldStateHead>(result.Head);

        Assert.Equal(CanonicalWorldStateCommitStatus.Committed, result.Status);
        Assert.True(result.AdvancedHead);
        Assert.True(File.Exists(head.PackagePath));
        Assert.Equal("first-state", await File.ReadAllTextAsync(head.PackagePath));
        Assert.False(File.Exists(sourcePath));

        var persistedHead = await coordinator.ReadHeadAsync("world-1");
        Assert.Equal(head, persistedHead);
    }

    [Fact]
    public async Task StaleExpectedHeadCannotAdvanceCanonicalStateOrConsumeCandidate()
    {
        using var fixture = new TemporaryStoreFixture();
        var coordinator = fixture.CreateCoordinator();
        var firstSource = await fixture.CreatePackageAsync("first-state");
        var first = await coordinator.CommitAsync(
            "world-1",
            expectedHeadRevisionId: null,
            CreateCapturedState(firstSource, deleteAfterStore: true));
        var firstHead = Assert.IsType<CanonicalWorldStateHead>(first.Head);
        var staleCandidate = await fixture.CreatePackageAsync("stale-state");

        var result = await coordinator.CommitAsync(
            "world-1",
            expectedHeadRevisionId: null,
            CreateCapturedState(staleCandidate, deleteAfterStore: true));

        Assert.Equal(CanonicalWorldStateCommitStatus.HeadChanged, result.Status);
        Assert.False(result.Succeeded);
        Assert.Equal(firstHead.RevisionId, result.ObservedHeadRevisionId);
        Assert.True(File.Exists(staleCandidate));

        var persistedHead = await coordinator.ReadHeadAsync("world-1");
        Assert.Equal(firstHead, persistedHead);
    }

    [Fact]
    public async Task IdenticalCandidateDoesNotCreateAnotherRevision()
    {
        using var fixture = new TemporaryStoreFixture();
        var coordinator = fixture.CreateCoordinator();
        var firstSource = await fixture.CreatePackageAsync("same-state");
        var first = await coordinator.CommitAsync(
            "world-1",
            expectedHeadRevisionId: null,
            CreateCapturedState(firstSource, deleteAfterStore: true));
        var firstHead = Assert.IsType<CanonicalWorldStateHead>(first.Head);
        var duplicateSource = await fixture.CreatePackageAsync("same-state");

        var duplicate = await coordinator.CommitAsync(
            "world-1",
            firstHead.RevisionId,
            CreateCapturedState(duplicateSource, deleteAfterStore: true));

        Assert.Equal(CanonicalWorldStateCommitStatus.Unchanged, duplicate.Status);
        Assert.True(duplicate.Succeeded);
        Assert.False(duplicate.AdvancedHead);
        Assert.Equal(firstHead, duplicate.Head);
        Assert.False(File.Exists(duplicateSource));
    }

    [Fact]
    public async Task FailedCandidateStorageLeavesPreviousHeadAuthoritative()
    {
        using var fixture = new TemporaryStoreFixture();
        var coordinator = fixture.CreateCoordinator();
        var firstSource = await fixture.CreatePackageAsync("known-good-state");
        var first = await coordinator.CommitAsync(
            "world-1",
            expectedHeadRevisionId: null,
            CreateCapturedState(firstSource, deleteAfterStore: true));
        var firstHead = Assert.IsType<CanonicalWorldStateHead>(first.Head);
        var missingPath = Path.Combine(fixture.RootPath, "missing.zip");

        await Assert.ThrowsAsync<FileNotFoundException>(() => coordinator.CommitAsync(
            "world-1",
            firstHead.RevisionId,
            CreateCapturedState(missingPath, deleteAfterStore: true)));

        var persistedHead = await coordinator.ReadHeadAsync("world-1");
        Assert.Equal(firstHead, persistedHead);
        Assert.True(File.Exists(firstHead.PackagePath));
    }

    private static CapturedState CreateCapturedState(string path, bool deleteAfterStore)
        => new(
            new StatePackage(Path.GetFileNameWithoutExtension(path), path),
            DateTimeOffset.UtcNow,
            deleteAfterStore);

    private sealed class TemporaryStoreFixture : IDisposable
    {
        private int _packageNumber;

        public TemporaryStoreFixture()
        {
            RootPath = Path.Combine(
                Path.GetTempPath(),
                "SharedWorlds-tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(RootPath);
        }

        public string RootPath { get; }

        public CanonicalWorldStateCommitCoordinator CreateCoordinator()
            => new(new FileSystemCanonicalWorldStateStore(Path.Combine(RootPath, "store")));

        public async Task<string> CreatePackageAsync(string content)
        {
            var path = Path.Combine(
                RootPath,
                $"candidate-{Interlocked.Increment(ref _packageNumber)}.zip");
            await File.WriteAllTextAsync(path, content);
            return path;
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(RootPath))
                {
                    Directory.Delete(RootPath, recursive: true);
                }
            }
            catch (IOException)
            {
                // Test cleanup is best-effort.
            }
            catch (UnauthorizedAccessException)
            {
                // Test cleanup is best-effort.
            }
        }
    }
}
