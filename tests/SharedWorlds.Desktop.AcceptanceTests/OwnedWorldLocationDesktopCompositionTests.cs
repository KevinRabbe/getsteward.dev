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
    public void NormalPeerConstructorAllocatesNoOwnedLocationJournalOrWorker()
    {
        var window = File.ReadAllText(FindRepositoryFile(
            "src/SharedWorlds.Desktop/MainWindow.xaml.cs"));
        var constructor = RequiredIndex(window, "public MainWindow()");
        var constructorEnd = RequiredIndex(window, "private void InitializeLiveRegionAnnouncements()", constructor);
        var body = window[constructor..constructorEnd];

        Assert.DoesNotContain("new LocalOwnedWorldLocationPublicationJournal(", body, StringComparison.Ordinal);
        Assert.DoesNotContain("new StewardOwnedWorldLocationPublicationTrigger(", body, StringComparison.Ordinal);
        Assert.Equal(1, CountOccurrences(body, "new OwnedWorldLocationObservedWorldStorage("));
        Assert.Contains(
            "localStorage,\n            RequestOwnedWorldLocationPublication);",
            body,
            StringComparison.Ordinal);
        Assert.Contains(
            "private IOwnedWorldLocationPublicationJournal? _ownedWorldLocationPublicationJournal;",
            window,
            StringComparison.Ordinal);
        Assert.Contains(
            "private StewardOwnedWorldLocationPublicationTrigger? _ownedWorldLocationPublicationTrigger;",
            window,
            StringComparison.Ordinal);
        Assert.Contains(
            "DisposeOwnedWorldLocationMigrationState();\n            DisposeRemoteRuntime();",
            window,
            StringComparison.Ordinal);
    }

    [Fact]
    public void MutationObserverIsInertUntilMigrationWorkerExists()
    {
        var publication = File.ReadAllText(FindRepositoryFile(
            "src/SharedWorlds.Desktop/MainWindow.OwnedWorldLocationPublication.cs"));

        var request = RequiredIndex(publication, "private void RequestOwnedWorldLocationPublication()");
        var nullableRead = RequiredIndex(
            publication,
            "Volatile.Read(ref _ownedWorldLocationPublicationTrigger)?.Request();",
            request);
        var ensure = RequiredIndex(publication, "EnsureOwnedWorldLocationMigrationState()", nullableRead);

        Assert.True(request < nullableRead);
        Assert.True(nullableRead < ensure);
        Assert.DoesNotContain("_remoteRuntime", publication[request..ensure], StringComparison.Ordinal);
    }

    [Fact]
    public void ExplicitRemoteActivationCreatesExactlyOneJournalAndOneBoundedWorker()
    {
        var window = File.ReadAllText(FindRepositoryFile(
            "src/SharedWorlds.Desktop/MainWindow.xaml.cs"));
        var publication = File.ReadAllText(FindRepositoryFile(
            "src/SharedWorlds.Desktop/MainWindow.OwnedWorldLocationPublication.cs"));
        var composition = File.ReadAllText(FindRepositoryFile(
            "src/SharedWorlds.Desktop/MainWindow.RemoteRuntime.cs"));

        Assert.Equal(1, CountOccurrences(publication, "new LocalOwnedWorldLocationPublicationJournal("));
        Assert.Equal(1, CountOccurrences(publication, "new StewardOwnedWorldLocationPublicationTrigger("));
        Assert.Contains("lock (_ownedWorldLocationMigrationStateGate)", publication, StringComparison.Ordinal);
        Assert.Contains("_ownedWorldLocationPublicationJournal ??=", publication, StringComparison.Ordinal);
        Assert.Contains("_ownedWorldLocationPublicationTrigger ??=", publication, StringComparison.Ordinal);
        Assert.DoesNotContain("new LocalOwnedWorldLocationPublicationJournal(", window, StringComparison.Ordinal);
        Assert.DoesNotContain("new StewardOwnedWorldLocationPublicationTrigger(", window, StringComparison.Ordinal);

        var method = RequiredIndex(composition, "internal async Task SetAuthenticatedRemoteRuntimeAsync(");
        var ensure = RequiredIndex(composition, "var migrationPublication = EnsureOwnedWorldLocationMigrationState();", method);
        var create = RequiredIndex(composition, "var next = StewardDesktopRemoteRuntime.Create(", ensure);
        var journal = RequiredIndex(composition, "migrationPublication.Journal,", create);

        Assert.True(method < ensure);
        Assert.True(ensure < create);
        Assert.True(create < journal);
    }

    [Fact]
    public void CandidateReplayAndRuntimeSwapHoldTheSharedPublicationGate()
    {
        var composition = File.ReadAllText(FindRepositoryFile(
            "src/SharedWorlds.Desktop/MainWindow.RemoteRuntime.cs"));
        var runtime = File.ReadAllText(FindRepositoryFile(
            "src/SharedWorlds.Desktop/StewardDesktopRemoteRuntime.cs"));

        var ensure = RequiredIndex(
            composition,
            "var migrationPublication = EnsureOwnedWorldLocationMigrationState();");
        var create = RequiredIndex(
            composition,
            "var next = StewardDesktopRemoteRuntime.Create(",
            ensure);
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
            "migrationPublication.Trigger.Request();",
            disposePrevious);

        Assert.True(ensure < create);
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

    [Fact]
    public void MigrationStateDisposalCancelsWorkerOutsideStateLockBeforeRemoteRuntimeDisposal()
    {
        var window = File.ReadAllText(FindRepositoryFile(
            "src/SharedWorlds.Desktop/MainWindow.xaml.cs"));
        var publication = File.ReadAllText(FindRepositoryFile(
            "src/SharedWorlds.Desktop/MainWindow.OwnedWorldLocationPublication.cs"));

        var dispose = RequiredIndex(publication, "private void DisposeOwnedWorldLocationMigrationState()");
        var stateLock = RequiredIndex(publication, "lock (_ownedWorldLocationMigrationStateGate)", dispose);
        var clearTrigger = RequiredIndex(publication, "_ownedWorldLocationPublicationTrigger = null;", stateLock);
        var lockEnd = RequiredIndex(publication, "}\n\n        // Cancel the bounded worker", clearTrigger);
        var triggerDispose = RequiredIndex(publication, "trigger?.Dispose();", lockEnd);
        Assert.True(dispose < stateLock);
        Assert.True(stateLock < clearTrigger);
        Assert.True(clearTrigger < lockEnd);
        Assert.True(lockEnd < triggerDispose);

        var closed = RequiredIndex(window, "Closed += (_, _) =>");
        var migrationDispose = RequiredIndex(window, "DisposeOwnedWorldLocationMigrationState();", closed);
        var remoteDispose = RequiredIndex(window, "DisposeRemoteRuntime();", migrationDispose);
        Assert.True(closed < migrationDispose);
        Assert.True(migrationDispose < remoteDispose);
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
