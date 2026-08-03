using Xunit;

namespace SharedWorlds.Desktop.AcceptanceTests;

public sealed class OwnedWorldLocationDesktopCompositionTests
{
    [Fact]
    public void RuntimeReusesItsSingleAccessSessionForOwnedLocationTransportAndReplay()
    {
        var runtime = File.ReadAllText(FindRepositoryFile(
            "src/SharedWorlds.Desktop/StewardDesktopRemoteRuntime.cs"));

        Assert.Equal(1, CountOccurrences(runtime, "new StewardAccessSession("));
        Assert.Contains(
            "var createdAccessSession = new StewardAccessSession(",
            runtime,
            StringComparison.Ordinal);
        Assert.Contains(
            "var ownedWorldLocations = new StewardOwnedWorldLocationClient(",
            runtime,
            StringComparison.Ordinal);
        Assert.Contains(
            "await createdAccessSession.GetAccessTokenAsync(cancellationToken)",
            runtime,
            StringComparison.Ordinal);
        Assert.Contains(
            "new StewardOwnedWorldLocationPublicationService(\n                    ownedWorldLocationPublicationJournal,\n                    ownedWorldLocations,\n                    installationId);",
            runtime,
            StringComparison.Ordinal);
        Assert.Contains(
            "new StewardOwnedWorldLocationCatalogReconciler(\n                    localStorage,\n                    ownedWorldLocationPublicationJournal,\n                    ownedWorldLocationPublication);",
            runtime,
            StringComparison.Ordinal);
        Assert.Contains(
            "public StewardOwnedWorldLocationClient OwnedWorldLocations { get; }",
            runtime,
            StringComparison.Ordinal);
        Assert.Contains(
            "OwnedWorldLocations = ownedWorldLocations;",
            runtime,
            StringComparison.Ordinal);
    }

    [Fact]
    public void DesktopOwnsOneDurableJournalAndOneBoundedMutationTrigger()
    {
        var window = File.ReadAllText(FindRepositoryFile(
            "src/SharedWorlds.Desktop/MainWindow.xaml.cs"));
        var composition = File.ReadAllText(FindRepositoryFile(
            "src/SharedWorlds.Desktop/MainWindow.RemoteRuntime.cs"));

        Assert.Equal(
            1,
            CountOccurrences(
                window,
                "new LocalOwnedWorldLocationPublicationJournal("));
        Assert.Equal(
            1,
            CountOccurrences(
                window,
                "new StewardOwnedWorldLocationPublicationTrigger("));
        Assert.Equal(
            1,
            CountOccurrences(
                window,
                "new OwnedWorldLocationObservedWorldStorage("));
        Assert.Contains(
            "private readonly IOwnedWorldLocationPublicationJournal _ownedWorldLocationPublicationJournal;",
            window,
            StringComparison.Ordinal);
        Assert.Contains(
            "private readonly StewardOwnedWorldLocationPublicationTrigger _ownedWorldLocationPublicationTrigger;",
            window,
            StringComparison.Ordinal);
        Assert.Contains(
            "private readonly SemaphoreSlim _ownedWorldLocationPublicationGate = new(1, 1);",
            window,
            StringComparison.Ordinal);
        Assert.Contains(
            "_storage = new OwnedWorldLocationObservedWorldStorage(\n            localStorage,\n            _ownedWorldLocationPublicationTrigger.Request);",
            window,
            StringComparison.Ordinal);
        Assert.Contains(
            "_storage,\n            _ownedWorldLocationPublicationJournal,\n            _workspaceRecoveryStore,",
            composition,
            StringComparison.Ordinal);
        Assert.Contains(
            "_ownedWorldLocationPublicationTrigger.Dispose();\n            DisposeRemoteRuntime();",
            window,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "_ownedWorldLocationPublicationJournal.Dispose",
            window,
            StringComparison.Ordinal);
    }

    [Fact]
    public void CandidateReplayAndRuntimeSwapHoldTheSharedPublicationGate()
    {
        var composition = File.ReadAllText(FindRepositoryFile(
            "src/SharedWorlds.Desktop/MainWindow.RemoteRuntime.cs"));
        var runtime = File.ReadAllText(FindRepositoryFile(
            "src/SharedWorlds.Desktop/StewardDesktopRemoteRuntime.cs"));

        var create = RequiredIndex(
            composition,
            "var next = StewardDesktopRemoteRuntime.Create(");
        var register = RequiredIndex(
            composition,
            "await next.OwnedWorldLocations.RegisterCurrentInstallationAsync(",
            create);
        var protocolCheck = RequiredIndex(composition, "registration.IsConflict", register);
        var gateWait = RequiredIndex(
            composition,
            "await _ownedWorldLocationPublicationGate.WaitAsync(cancellationToken);",
            protocolCheck);
        var gateHeld = RequiredIndex(
            composition,
            "publicationGateHeld = true;",
            gateWait);
        var reconcileAndReplay = RequiredIndex(
            composition,
            "await next.ReconcileAndReplayOwnedWorldLocationsAsync(cancellationToken);",
            gateHeld);
        var cancellationFence = RequiredIndex(
            composition,
            "cancellationToken.ThrowIfCancellationRequested();",
            reconcileAndReplay);
        var activate = RequiredIndex(
            composition,
            "previous = Interlocked.Exchange(ref _remoteRuntime, next);",
            cancellationFence);
        var disposeCandidate = RequiredIndex(
            composition,
            "next.Dispose();",
            activate);
        var releaseGate = RequiredIndex(
            composition,
            "_ownedWorldLocationPublicationGate.Release();",
            disposeCandidate);
        var disposePrevious = RequiredIndex(
            composition,
            "previous?.Dispose();",
            releaseGate);
        var followUpRequest = RequiredIndex(
            composition,
            "_ownedWorldLocationPublicationTrigger.Request();",
            disposePrevious);

        Assert.True(create < register);
        Assert.True(register < protocolCheck);
        Assert.True(protocolCheck < gateWait);
        Assert.True(gateWait < gateHeld);
        Assert.True(gateHeld < reconcileAndReplay);
        Assert.True(reconcileAndReplay < cancellationFence);
        Assert.True(cancellationFence < activate);
        Assert.True(activate < disposeCandidate);
        Assert.True(disposeCandidate < releaseGate);
        Assert.True(releaseGate < disposePrevious);
        Assert.True(disposePrevious < followUpRequest);
        Assert.Equal(
            1,
            CountOccurrences(
                composition,
                "Interlocked.Exchange(ref _remoteRuntime, next)"));
        Assert.Contains(
            "Steward did not confirm this Safe World installation registration.",
            composition,
            StringComparison.Ordinal);

        var reconcile = RequiredIndex(
            runtime,
            "await _ownedWorldLocationCatalogReconciler.ReconcileAsync(cancellationToken);");
        var replay = RequiredIndex(
            runtime,
            "await _ownedWorldLocationPublication.ReplayAllAsync(cancellationToken);",
            reconcile);
        Assert.True(reconcile < replay);
    }

    [Fact]
    public void LivePublicationUsesTheSameGateAndCurrentRuntimeOnly()
    {
        var publication = File.ReadAllText(FindRepositoryFile(
            "src/SharedWorlds.Desktop/MainWindow.OwnedWorldLocationPublication.cs"));
        var composition = File.ReadAllText(FindRepositoryFile(
            "src/SharedWorlds.Desktop/MainWindow.RemoteRuntime.cs"));

        var gateWait = RequiredIndex(
            publication,
            "await _ownedWorldLocationPublicationGate.WaitAsync(cancellationToken);");
        var currentRuntime = RequiredIndex(
            publication,
            "var remote = Volatile.Read(ref _remoteRuntime);",
            gateWait);
        var nullExit = RequiredIndex(publication, "if (remote is null)", currentRuntime);
        var reconcile = RequiredIndex(
            publication,
            "await remote.ReconcileAndReplayOwnedWorldLocationsAsync(cancellationToken);",
            nullExit);
        var gateRelease = RequiredIndex(
            publication,
            "_ownedWorldLocationPublicationGate.Release();",
            reconcile);

        Assert.True(gateWait < currentRuntime);
        Assert.True(currentRuntime < nullExit);
        Assert.True(nullExit < reconcile);
        Assert.True(reconcile < gateRelease);
        Assert.Contains(
            "Interlocked.Exchange(ref _remoteRuntime, null)?.Dispose();",
            composition,
            StringComparison.Ordinal);
        Assert.Contains(
            "Exact pending work remains durable",
            publication,
            StringComparison.Ordinal);
    }

    private static int RequiredIndex(string source, string value, int startIndex = 0)
    {
        var index = source.IndexOf(value, startIndex, StringComparison.Ordinal);
        Assert.True(index >= 0, $"Required source fragment was not found: {value}");
        return index;
    }

    private static int CountOccurrences(string source, string value)
    {
        var count = 0;
        var index = 0;
        while ((index = source.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }

        return count;
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
