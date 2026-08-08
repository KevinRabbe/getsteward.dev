using Xunit;

namespace SharedWorlds.Desktop.AcceptanceTests;

public sealed class SteamCloudPeerAuthorityFenceCompositionTests
{
    [Fact]
    public void FenceStoreUsesSteamRemoteStorageWithoutOwningSteamLifetime()
    {
        var source = ReadStore();

        Assert.Contains(
            "internal sealed class SteamCloudPeerAuthorityFenceStore : IPeerAuthorityFenceStore",
            source,
            StringComparison.Ordinal);
        Assert.Contains("SteamRemoteStorage.FileWrite(", source, StringComparison.Ordinal);
        Assert.Contains("SteamRemoteStorage.FileRead(", source, StringComparison.Ordinal);
        Assert.Contains("SteamRemoteStorage.FileExists(", source, StringComparison.Ordinal);
        Assert.Contains("SteamRemoteStorage.GetFileSize(", source, StringComparison.Ordinal);
        Assert.DoesNotContain("SteamAPI.Init", source, StringComparison.Ordinal);
        Assert.DoesNotContain("SteamAPI.Shutdown", source, StringComparison.Ordinal);
    }

    [Fact]
    public void CloudMustBeEnabledForAccountAndApp()
    {
        var source = ReadStore();

        Assert.Contains("SteamRemoteStorage.IsCloudEnabledForAccount()", source, StringComparison.Ordinal);
        Assert.Contains("SteamRemoteStorage.IsCloudEnabledForApp()", source, StringComparison.Ordinal);
        Assert.Contains("stale-device fencing cannot be guaranteed", source, StringComparison.Ordinal);
    }

    [Fact]
    public void FenceDocumentIsSmallVersionedAndBoundToCurrentSteamAccount()
    {
        var source = ReadStore();

        Assert.Contains("private const int SchemaVersion = 1;", source, StringComparison.Ordinal);
        Assert.Contains("private const int MaximumFenceBytes = 16 * 1024;", source, StringComparison.Ordinal);
        Assert.Contains("document.AccountSteamId", source, StringComparison.Ordinal);
        Assert.Contains("_platform.LocalSteamId.m_SteamID", source, StringComparison.Ordinal);
        Assert.Contains("document.Generation == 0", source, StringComparison.Ordinal);
        Assert.Contains("Guid.TryParseExact(document.StateRevisionId, \"N\"", source, StringComparison.Ordinal);
    }

    [Fact]
    public void EveryCloudWriteIsReadBackAndVerified()
    {
        var source = ReadStore();
        var write = RequiredIndex(source, "SteamRemoteStorage.FileWrite(fileName, bytes, bytes.Length)");
        var reload = RequiredIndex(source, "var verified = LoadCore(fence.WorldId)", write);
        var compare = RequiredIndex(source, "if (!EquivalentFence(verified, fence))", reload);

        Assert.True(write < reload);
        Assert.True(reload < compare);
    }

    [Fact]
    public void GenerationCannotMoveBackwardOrConflictAtSameGeneration()
    {
        var source = ReadStore();
        var method = RequiredIndex(source, "private static void ValidateMonotonicTransition(");
        var backward = RequiredIndex(source, "next.Generation < current.Generation", method);
        var forward = RequiredIndex(source, "next.Generation > current.Generation", backward);
        var sameHolder = RequiredIndex(source, "SameUser(current.Holder, next.Holder)", forward);
        var sameRevision = RequiredIndex(source, "current.StateRevisionId != next.StateRevisionId", sameHolder);
        var sameGenerationTransition = RequiredIndex(
            source,
            "current.State == PeerAuthorityFenceState.Relinquishing &&\n                      next.State == PeerAuthorityFenceState.Observed",
            sameRevision);

        Assert.True(backward < forward);
        Assert.True(forward < sameHolder);
        Assert.True(sameHolder < sameRevision);
        Assert.True(sameRevision < sameGenerationTransition);
    }

    [Fact]
    public void ActiveAndRelinquishingStatesCannotClaimImpossibleLocalIdentity()
    {
        var source = ReadStore();

        Assert.Contains(
            "fence.State == PeerAuthorityFenceState.Active &&",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "holderSteamId != _platform.LocalSteamId.m_SteamID",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "fence.State == PeerAuthorityFenceState.Relinquishing &&",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "holderSteamId == _platform.LocalSteamId.m_SteamID",
            source,
            StringComparison.Ordinal);
    }

    [Fact]
    public void FenceStoreNeverCarriesWorldPayloadOrRevisionStorage()
    {
        var source = ReadStore();

        Assert.DoesNotContain("IWorldStorage", source, StringComparison.Ordinal);
        Assert.DoesNotContain("StatePackage", source, StringComparison.Ordinal);
        Assert.DoesNotContain("OpenRevision", source, StringComparison.Ordinal);
        Assert.DoesNotContain("StoreRevision", source, StringComparison.Ordinal);
        Assert.DoesNotContain("EnvironmentRevision", source, StringComparison.Ordinal);
        Assert.Contains(
            "World payloads,\n/// environments, history, and saves never pass through this store.",
            source,
            StringComparison.Ordinal);
    }

    private static string ReadStore()
        => File.ReadAllText(FindRepositoryFile(
            "src/SharedWorlds.Desktop/SteamCloudPeerAuthorityFenceStore.cs"));

    private static int RequiredIndex(string source, string value, int startIndex = 0)
    {
        var index = source.IndexOf(value, startIndex, StringComparison.Ordinal);
        Assert.True(index >= 0, $"Required source fragment was not found: {value}");
        return index;
    }

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
}