using Xunit;

namespace SharedWorlds.Desktop.AcceptanceTests;

public sealed class LiveMemberRemovalCompositionTests
{
    [Fact]
    public void LiveRemovalRevokesBeforeCanonicalMembershipPersistence()
    {
        var source = ReadRepositoryFile(
            "src/SharedWorlds.Infrastructure/Sessions/PeerWorldMemberRemovalService.cs");

        var handoffGuard = RequiredIndex(source, "if (liveLobby.RequestedHost is not null)");
        var revoke = RequiredIndex(source, "_liveRevocations.Revoke(", handoffGuard);
        var save = RequiredIndex(source, "await _storage.SaveWorldAsync(updated, cancellationToken);", revoke);

        Assert.True(handoffGuard < revoke);
        Assert.True(revoke < save);
        Assert.Contains("the deny fence deliberately remains active", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Port72RevocationTargetsExactRemoteWorldAndGeneration()
    {
        var source = ReadRepositoryFile(
            "src/SharedWorlds.Desktop/SteamPeerGameDatagramBridge.cs");

        Assert.Contains("_liveRevocations.Revoked += OnMemberRevoked;", source, StringComparison.Ordinal);
        Assert.Contains("_liveRevocations.Revoked -= OnMemberRevoked;", source, StringComparison.Ordinal);

        var open = RequiredIndex(source, "case MessageKind.Open when context.State == HostBridgeState.AwaitingOpen:");
        var bindWorld = RequiredIndex(source, "context.WorldId = open.WorldId;", open);
        var bindGeneration = RequiredIndex(source, "context.AuthorityGeneration = open.AuthorityGeneration;", bindWorld);
        var authorize = RequiredIndex(source, "Task.Run(() => AuthorizeHostBridgeAsync(context, open))", bindGeneration);
        Assert.True(open < bindWorld);
        Assert.True(bindWorld < bindGeneration);
        Assert.True(bindGeneration < authorize);

        var handler = RequiredIndex(source, "private void OnMemberRevoked(PeerWorldLiveMemberRevocation revocation)");
        var remoteFilter = RequiredIndex(source, "context.RemoteSteamId != revokedSteamId", handler);
        var worldFilter = RequiredIndex(source, "contextWorldId != revocation.WorldId", remoteFilter);
        var generationFilter = RequiredIndex(source, "context.AuthorityGeneration != revocation.AuthorityGeneration", worldFilter);
        var targetedClose = RequiredIndex(source, "FailAndRemove(", generationFilter);
        var nextMethod = RequiredIndex(source, "private void AcceptIncomingConnection", targetedClose);
        var handlerBody = source[handler..nextMethod];

        Assert.True(handler < remoteFilter);
        Assert.True(remoteFilter < worldFilter);
        Assert.True(worldFilter < generationFilter);
        Assert.True(generationFilter < targetedClose);
        Assert.DoesNotContain("_connections.Values.ToArray()", handlerBody, StringComparison.Ordinal);
        Assert.DoesNotContain("Dispose();", handlerBody, StringComparison.Ordinal);
    }

    [Fact]
    public void Port72HostSocketAttachCannotRacePastRevocationCancellation()
    {
        var source = ReadRepositoryFile(
            "src/SharedWorlds.Desktop/SteamPeerGameDatagramBridge.cs");

        var attachCall = RequiredIndex(source, "if (!context.TryAttachHostSocket(socket))");
        var ready = RequiredIndex(source, "context.State = HostBridgeState.Ready;", attachCall);
        var attachMethod = RequiredIndex(source, "public bool TryAttachHostSocket(Socket socket)");
        var gate = RequiredIndex(source, "lock (_resourceGate)", attachMethod);
        var canceled = RequiredIndex(source, "Cancellation.IsCancellationRequested", gate);
        var assignment = RequiredIndex(source, "HostSocket = socket;", canceled);

        Assert.True(attachCall < ready);
        Assert.True(attachMethod < gate);
        Assert.True(gate < canceled);
        Assert.True(canceled < assignment);
    }

    [Fact]
    public void RuntimeSharesOneRemovalFenceAndOneAuthorityMutationGate()
    {
        var source = ReadRepositoryFile("src/SharedWorlds.Desktop/StewardDesktopPeerRuntime.cs");

        Assert.Equal(1, CountOccurrences(source, "new PeerWorldLiveMemberRevocationRegistry()"));
        Assert.Equal(1, CountOccurrences(source, "new PeerWorldLiveAuthorityMutationGate()"));
        Assert.Contains("new PeerWorldLiveAuthoritySessionCoordinator(", source, StringComparison.Ordinal);

        var membership = RequiredIndex(source, "var membership = new PeerWorldMembershipService(");
        var membershipRevocation = RequiredIndex(source, "liveMemberRevocations,", membership);
        var membershipMutation = RequiredIndex(source, "liveAuthorityMutations);", membershipRevocation);
        var removal = RequiredIndex(source, "var memberRemoval = new PeerWorldMemberRemovalService(", membershipMutation);
        var removalRevocation = RequiredIndex(source, "liveMemberRevocations,", removal);
        var removalMutation = RequiredIndex(source, "liveAuthorityMutations);", removalRevocation);
        var bridge = RequiredIndex(source, "gameBridge = new SteamPeerGameDatagramBridge(", removalMutation);
        var bridgeRevocation = RequiredIndex(source, "liveMemberRevocations);", bridge);

        Assert.True(membership < membershipRevocation);
        Assert.True(membershipRevocation < membershipMutation);
        Assert.True(membershipMutation < removal);
        Assert.True(removal < removalRevocation);
        Assert.True(removalRevocation < removalMutation);
        Assert.True(removalMutation < bridge);
        Assert.True(bridge < bridgeRevocation);
    }

    [Fact]
    public void HandoffAndMembershipMutationShareSerializationBoundary()
    {
        var coordinator = ReadRepositoryFile(
            "src/SharedWorlds.Infrastructure/Sessions/PeerWorldLiveAuthoritySessionCoordinator.cs");
        var membership = ReadRepositoryFile(
            "src/SharedWorlds.Infrastructure/Sessions/PeerWorldMembershipService.cs");
        var removal = ReadRepositoryFile(
            "src/SharedWorlds.Infrastructure/Sessions/PeerWorldMemberRemovalService.cs");

        var request = RequiredIndex(coordinator, "public async Task RequestHandoffAsync(");
        RequiredIndex(coordinator, "await _mutations.EnterAsync(cancellationToken)", request);
        var complete = RequiredIndex(coordinator, "public async Task CompleteHandoffAsync(", request);
        RequiredIndex(coordinator, "await _mutations.EnterAsync(cancellationToken)", complete);
        RequiredIndex(membership, "await _liveAuthorityMutations.EnterAsync(cancellationToken)");
        RequiredIndex(removal, "await _liveAuthorityMutations.EnterAsync(cancellationToken)");

        Assert.Contains("_inner.MarkHostStartingAsync", coordinator, StringComparison.Ordinal);
        Assert.Contains("_inner.MarkHostReadyAsync", coordinator, StringComparison.Ordinal);
        Assert.Contains("_inner.EndHostPresenceAsync", coordinator, StringComparison.Ordinal);
    }

    [Fact]
    public void AccessDialogDescribesLiveRevocationInsteadOfInactiveOnlyRemoval()
    {
        var source = ReadRepositoryFile("src/SharedWorlds.Desktop/PeerWorldAccessDialog.cs");

        Assert.Contains("active peer transfer and game sessions", source, StringComparison.Ordinal);
        Assert.Contains("host handoff", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("World must be inactive", source, StringComparison.Ordinal);
        Assert.DoesNotContain("available only while the World is inactive", source, StringComparison.Ordinal);
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
