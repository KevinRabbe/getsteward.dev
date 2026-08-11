using Xunit;

namespace SharedWorlds.Desktop.AcceptanceTests;

public sealed class LiveMemberRevocationFenceCompositionTests
{
    [Fact]
    public void PeerRuntimeComposesOneSharedLiveRevocationRegistry()
    {
        var source = ReadRepositoryFile("src/SharedWorlds.Desktop/StewardDesktopPeerRuntime.cs");

        Assert.Equal(
            1,
            CountOccurrences(
                source,
                "new PeerWorldLiveMemberRevocationRegistry()"));
        Assert.Contains(
            "var liveMemberRevocations = new PeerWorldLiveMemberRevocationRegistry();",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "PeerWorldLiveMemberRevocationRegistry LiveMemberRevocations",
            source,
            StringComparison.Ordinal);
    }

    [Fact]
    public void SameRegistryFencesCatchUpMembershipGameAdmissionAndRemoval()
    {
        var source = ReadRepositoryFile("src/SharedWorlds.Desktop/StewardDesktopPeerRuntime.cs");

        AssertConstructorUses(
            source,
            "var catchUpRouter = new PeerWorldCatchUpRequestRouter(",
            "liveMemberRevocations");
        AssertConstructorUses(
            source,
            "var membership = new PeerWorldMembershipService(",
            "liveMemberRevocations");
        AssertConstructorUses(
            source,
            "var memberRemoval = new PeerWorldMemberRemovalService(",
            "liveMemberRevocations");
        AssertConstructorUses(
            source,
            "var gameBridgeAdmission = new PeerGameDatagramBridgeAdmissionService(",
            "liveMemberRevocations");
    }

    [Fact]
    public void CatchUpUsesExactMemberGenerationCancellationToken()
    {
        var source = ReadRepositoryFile("src/SharedWorlds.Infrastructure/Sessions/PeerWorldCatchUp.cs");

        var initialDeny = RequiredIndex(source, "_liveRevocations?.ThrowIfRevoked(");
        var linked = RequiredIndex(source, "CancellationTokenSource.CreateLinkedTokenSource(", initialDeny);
        var token = RequiredIndex(source, "_liveRevocations.GetCancellationToken(", linked);
        var bootstrap = RequiredIndex(source, "services.Bootstrap.BootstrapAsync(", token);
        var observer = RequiredIndex(source, "services.ObserverSync.SynchronizeAsync(", bootstrap);

        Assert.True(initialDeny < linked);
        Assert.True(linked < token);
        Assert.True(token < bootstrap);
        Assert.True(bootstrap < observer);
        Assert.Contains("operationToken", source, StringComparison.Ordinal);
    }

    [Fact]
    public void GameAdmissionChecksRevocationBeforeAndAfterAuthorityReads()
    {
        var source = ReadRepositoryFile("src/SharedWorlds.Infrastructure/Sessions/PeerGameDatagramBridgeAdmission.cs");
        const string deny = "_liveRevocations?.ThrowIfRevoked(";

        var firstDeny = RequiredIndex(source, deny);
        var lobbyRead = RequiredIndex(source, "var lobby = await _lobby.GetAsync", firstDeny);
        var presenceRead = RequiredIndex(source, "var presence = await _presence.GetAsync", lobbyRead);
        var secondDeny = RequiredIndex(source, deny, presenceRead);
        var grant = RequiredIndex(source, "return new PeerGameDatagramBridgeGrant(", secondDeny);

        Assert.True(firstDeny < lobbyRead);
        Assert.True(lobbyRead < presenceRead);
        Assert.True(presenceRead < secondDeny);
        Assert.True(secondDeny < grant);
    }

    [Fact]
    public void CanonicalReadmissionRestoresFenceOnlyAfterPersistence()
    {
        var source = ReadRepositoryFile("src/SharedWorlds.Infrastructure/Sessions/PeerWorldMembershipService.cs");
        var save = RequiredIndex(source, "await _storage.SaveWorldAsync(updated, cancellationToken);");
        var restore = RequiredIndex(
            source,
            "_liveRevocations?.Restore(worldId, authority.Generation, newMember);",
            save);

        Assert.True(save < restore);
    }

    [Fact]
    public void ManagedHostEndCancelsAndClearsExactGenerationFence()
    {
        var runtime = ReadRepositoryFile("src/SharedWorlds.Desktop/StewardDesktopPeerRuntime.cs");
        var registry = ReadRepositoryFile("src/SharedWorlds.Infrastructure/Sessions/PeerWorldLiveMemberRevocation.cs");

        var endHandler = RequiredIndex(runtime, "private void OnManagedHostPresenceEnded(");
        var clear = RequiredIndex(runtime, "_liveMemberRevocations.ClearWorld(", endHandler);
        var restoreBridge = RequiredIndex(runtime, "RestoreGameBridge();", clear);
        Assert.True(endHandler < clear);
        Assert.True(clear < restoreBridge);

        var clearMethod = RequiredIndex(registry, "public int ClearWorld(");
        var cancel = RequiredIndex(registry, "CancelWithoutPropagation(cancellation);", clearMethod);
        var dispose = RequiredIndex(registry, "cancellation.Dispose();", cancel);
        Assert.True(clearMethod < cancel);
        Assert.True(cancel < dispose);
    }

    [Fact]
    public void LiveRemovalRequiresBothRevocationAndMutationBoundaries()
    {
        var runtime = ReadRepositoryFile("src/SharedWorlds.Desktop/StewardDesktopPeerRuntime.cs");
        var removal = ReadRepositoryFile("src/SharedWorlds.Infrastructure/Sessions/PeerWorldMemberRemovalService.cs");

        var memberRemoval = RequiredIndex(runtime, "var memberRemoval = new PeerWorldMemberRemovalService(");
        var revocation = RequiredIndex(runtime, "liveMemberRevocations,", memberRemoval);
        var mutation = RequiredIndex(runtime, "liveAuthorityMutations);", revocation);
        Assert.True(memberRemoval < revocation);
        Assert.True(revocation < mutation);
        Assert.Contains("_liveRevocations.Revoke(", removal, StringComparison.Ordinal);
        Assert.Contains("Remove access cannot run while a host handoff is in progress", removal, StringComparison.Ordinal);
    }

    private static void AssertConstructorUses(
        string source,
        string constructorStart,
        string dependency)
    {
        var start = RequiredIndex(source, constructorStart);
        var end = RequiredIndex(source, ");", start);
        var block = source[start..(end + 2)];
        Assert.Contains(dependency, block, StringComparison.Ordinal);
    }

    private static int CountOccurrences(string source, string value)
    {
        var count = 0;
        var offset = 0;
        while (true)
        {
            var index = source.IndexOf(value, offset, StringComparison.Ordinal);
            if (index < 0)
            {
                return count;
            }

            count++;
            offset = index + value.Length;
        }
    }

    private static int RequiredIndex(string source, string value, int startIndex = 0)
    {
        var index = source.IndexOf(value, startIndex, StringComparison.Ordinal);
        Assert.True(index >= 0, $"Required source fragment was not found: {value}");
        return index;
    }

    private static string ReadRepositoryFile(string relativePath)
    {
        var workspace = Environment.GetEnvironmentVariable("GITHUB_WORKSPACE");
        if (!string.IsNullOrWhiteSpace(workspace))
        {
            var candidate = Path.Combine(workspace, relativePath);
            if (File.Exists(candidate))
            {
                return File.ReadAllText(candidate);
            }
        }

        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, relativePath);
            if (File.Exists(candidate))
            {
                return File.ReadAllText(candidate);
            }
        }

        throw new FileNotFoundException($"Could not locate repository file '{relativePath}'.");
    }
}
