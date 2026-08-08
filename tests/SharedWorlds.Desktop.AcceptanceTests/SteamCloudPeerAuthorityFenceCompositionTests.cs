using Xunit;

namespace SharedWorlds.Desktop.AcceptanceTests;

public sealed class SteamCloudPeerAuthorityFenceCompositionTests
{
    [Fact]
    public void FenceStoreUsesSteamRemoteStorageWithoutOwningSteamLifetime()
    {
        var source = ReadStore();

        Assert.Contains(
            "internal sealed class SteamCloudPeerAuthorityFenceStore : IPeerAuthorityActiveRevisionFenceStore",
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
        Assert.Contains("peer restart fencing is unavailable", source, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void FenceDocumentIsSmallVersionedAndBoundToSteamAccountAndInstallation()
    {
        var source = ReadStore();

        Assert.Contains("private const int SchemaVersion = 2;", source, StringComparison.Ordinal);
        Assert.Contains("private const int MaximumFenceBytes = 16 * 1024;", source, StringComparison.Ordinal);
        Assert.Contains("string WriterInstallationId", source, StringComparison.Ordinal);
        Assert.Contains("document.AccountSteamId", source, StringComparison.Ordinal);
        Assert.Contains("_platform.LocalSteamId.m_SteamID", source, StringComparison.Ordinal);
        Assert.Contains("document.WriterInstallationId", source, StringComparison.Ordinal);
        Assert.Contains("_installationId", source, StringComparison.Ordinal);
        Assert.Contains("document.Generation == 0", source, StringComparison.Ordinal);
        Assert.Contains("Guid.TryParseExact(document.StateRevisionId, \"N\"", source, StringComparison.Ordinal);
    }

    [Fact]
    public void ActiveFenceFromDifferentStewardInstallationFailsClosed()
    {
        var source = ReadStore();
        var active = RequiredIndex(
            source,
            "document.State == PeerAuthorityFenceState.Active &&");
        var installation = RequiredIndex(
            source,
            "document.WriterInstallationId,\n                _installationId,",
            active);
        var failure = RequiredIndex(
            source,
            "belongs to a different Steward installation",
            installation);

        Assert.True(active < installation);
        Assert.True(installation < failure);
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
    public void GenericGenerationTransitionCannotReplaceRevisionAtSameGeneration()
    {
        var source = ReadStore();
        var method = RequiredIndex(source, "private static void ValidateMonotonicTransition(");
        var backward = RequiredIndex(source, "next.Generation < current.Generation", method);
        var forward = RequiredIndex(source, "next.Generation > current.Generation", backward);
        var sameHolder = RequiredIndex(source, "SameUser(current.Holder, next.Holder)", forward);
        var sameRevision = RequiredIndex(source, "current.StateRevisionId != next.StateRevisionId", sameHolder);

        Assert.True(backward < forward);
        Assert.True(forward < sameHolder);
        Assert.True(sameHolder < sameRevision);
    }

    [Fact]
    public void ActiveRevisionAdvanceRequiresExactExpectedFenceOrExactRetry()
    {
        var source = ReadStore();
        var method = RequiredIndex(source, "public async Task<PeerAuthorityFence> AdvanceActiveRevisionAsync(");
        var retry = RequiredIndex(source, "if (EquivalentFence(current, next))", method);
        var active = RequiredIndex(source, "current.State != PeerAuthorityFenceState.Active", retry);
        var generation = RequiredIndex(source, "current.Generation != generation", active);
        var expectedRevision = RequiredIndex(source, "current.StateRevisionId != expectedStateRevisionId", generation);
        var holder = RequiredIndex(source, "!SameUser(current.Holder, holder)", expectedRevision);
        var write = RequiredIndex(source, "WriteAndVerifyCore(next);", holder);

        Assert.True(retry < active);
        Assert.True(active < generation);
        Assert.True(generation < expectedRevision);
        Assert.True(expectedRevision < holder);
        Assert.True(holder < write);
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
            "World payloads, environments,\n/// history, and saves never pass through this store.",
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
