using Xunit;

namespace SharedWorlds.Desktop.AcceptanceTests;

public sealed class OwnedWorldSnapshotDesktopCompositionTests
{
    [Fact]
    public void RuntimeReusesSingleSessionApiTransferCacheAndCanonicalStorage()
    {
        var runtime = Read("src/SharedWorlds.Desktop/StewardDesktopRemoteRuntime.cs");

        Assert.Equal(1, CountOccurrences(runtime, "new StewardAccessSession("));
        Assert.Equal(1, CountOccurrences(runtime, "new VerifiedPackageCache("));
        Assert.Equal(1, CountOccurrences(runtime, "new StewardPrivateSnapshotTransferClient("));
        Assert.Equal(
            1,
            CountOccurrences(
                runtime,
                "new StewardPrivateSnapshotRevisionEvidenceClient("));
        Assert.Equal(1, CountOccurrences(runtime, "new StewardOwnedWorldSnapshotPublisher("));
        Assert.Contains(
            "new StewardPrivateSnapshotTransferClient(\n                apiClient,\n                transferClient,\n                verifiedCache,\n                GetAccessTokenAsync);",
            runtime,
            StringComparison.Ordinal);
        Assert.Contains(
            "new StewardPrivateSnapshotRevisionEvidenceClient(\n                    apiClient,\n                    GetAccessTokenAsync);",
            runtime,
            StringComparison.Ordinal);
        Assert.Contains(
            "new StewardOwnedWorldSnapshotPublisher(\n                localStorage,\n                privateSnapshotTransfers,\n                privateSnapshotRevisionEvidence);",
            runtime,
            StringComparison.Ordinal);
        Assert.Contains(
            "=> _ownedWorldSnapshotPublisher.PublishAllCurrentAsync(cancellationToken);",
            runtime,
            StringComparison.Ordinal);
    }

    [Fact]
    public void LivePassPublishesLocationBeforeBytesAndRevisionEvidenceUnderExistingGate()
    {
        var publication = Read(
            "src/SharedWorlds.Desktop/MainWindow.OwnedWorldLocationPublication.cs");
        var activation = Read("src/SharedWorlds.Desktop/MainWindow.RemoteRuntime.cs");
        var publisher = Read(
            "src/SharedWorlds.Infrastructure/Remote/StewardOwnedWorldSnapshotPublisher.cs");

        var gate = RequiredIndex(
            publication,
            "await _ownedWorldLocationPublicationGate.WaitAsync(cancellationToken);");
        var currentRuntime = RequiredIndex(
            publication,
            "var remote = Volatile.Read(ref _remoteRuntime);",
            gate);
        var location = RequiredIndex(
            publication,
            "await remote.ReconcileAndReplayOwnedWorldLocationsAsync(cancellationToken);",
            currentRuntime);
        var snapshot = RequiredIndex(
            publication,
            "await remote.PublishCurrentOwnedWorldSnapshotsAsync(cancellationToken);",
            location);
        var release = RequiredIndex(
            publication,
            "_ownedWorldLocationPublicationGate.Release();",
            snapshot);
        var upload = RequiredIndex(publisher, "var upload = await _uploadAsync(");
        var byteConfirmation = RequiredIndex(
            publisher,
            "_byteConfirmed[world.Id] = currentHead;",
            upload);
        var evidence = RequiredIndex(
            publisher,
            "var evidence = await _publishEvidenceAsync(",
            byteConfirmation);
        var fullConfirmation = RequiredIndex(
            publisher,
            "_fullyConfirmed[world.Id] = currentHead;",
            evidence);

        Assert.True(gate < currentRuntime);
        Assert.True(currentRuntime < location);
        Assert.True(location < snapshot);
        Assert.True(snapshot < release);
        Assert.True(upload < byteConfirmation);
        Assert.True(byteConfirmation < evidence);
        Assert.True(evidence < fullConfirmation);
        Assert.DoesNotContain(
            "PublishCurrentOwnedWorldSnapshotsAsync",
            activation,
            StringComparison.Ordinal);
        Assert.Contains(
            "migrationPublication.Trigger.Request();",
            activation,
            StringComparison.Ordinal);
    }

    [Fact]
    public void EvidenceRepairRetainsByteConfirmationWithoutCompletingHead()
    {
        var publisher = Read(
            "src/SharedWorlds.Infrastructure/Remote/StewardOwnedWorldSnapshotPublisher.cs");

        Assert.Contains(
            "private readonly Dictionary<WorldId, ConfirmedHead> _byteConfirmed = [];",
            publisher,
            StringComparison.Ordinal);
        Assert.Contains(
            "private readonly Dictionary<WorldId, ConfirmedHead> _fullyConfirmed = [];",
            publisher,
            StringComparison.Ordinal);
        Assert.Contains(
            "if (!_byteConfirmed.TryGetValue(world.Id, out var byteConfirmed) ||",
            publisher,
            StringComparison.Ordinal);
        Assert.Contains(
            "Steward private revision evidence publication ended with status",
            publisher,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "_byteConfirmed.Remove(world.Id);\n            throw",
            publisher,
            StringComparison.Ordinal);
    }

    [Fact]
    public void AuxiliaryFailureCannotRewriteLocalCommitSemantics()
    {
        var publication = Read(
            "src/SharedWorlds.Desktop/MainWindow.OwnedWorldLocationPublication.cs");
        var window = Read("src/SharedWorlds.Desktop/MainWindow.xaml.cs");

        Assert.Contains(
            "Local World changes remain committed",
            publication,
            StringComparison.Ordinal);
        Assert.Contains(
            "resumable snapshot transfer can continue",
            publication,
            StringComparison.Ordinal);
        Assert.Contains(
            "new StewardOwnedWorldLocationPublicationTrigger(\n                    PublishCurrentOwnedWorldLocationsAsync,",
            publication,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "new StewardOwnedWorldLocationPublicationTrigger(",
            window,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "await remote.PublishCurrentOwnedWorldSnapshotsAsync",
            window,
            StringComparison.Ordinal);
    }

    [Fact]
    public void LocationAndSnapshotPublicationShareOneCanonicalResolver()
    {
        var resolver = Read(
            "src/SharedWorlds.Infrastructure/Remote/OwnedWorldCanonicalSnapshotResolver.cs");
        var reconciler = Read(
            "src/SharedWorlds.Infrastructure/Remote/StewardOwnedWorldLocationCatalogReconciler.cs");
        var publisher = Read(
            "src/SharedWorlds.Infrastructure/Remote/StewardOwnedWorldSnapshotPublisher.cs");

        Assert.Contains("WorldSharingMode.LocalOnly", resolver, StringComparison.Ordinal);
        Assert.Contains("state.EnvironmentRevisionId != environmentRevisionId", resolver, StringComparison.Ordinal);
        Assert.Contains("IsRevisionPayloadAvailableAsync", resolver, StringComparison.Ordinal);
        Assert.Equal(
            1,
            CountOccurrences(
                reconciler,
                "new OwnedWorldCanonicalSnapshotResolver("));
        Assert.Equal(
            1,
            CountOccurrences(
                publisher,
                "new OwnedWorldCanonicalSnapshotResolver("));
        Assert.DoesNotContain(
            "LoadStateRevisionAsync",
            publisher,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "LoadEnvironmentRevisionAsync",
            publisher,
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

    private static string Read(string relativePath)
        => File.ReadAllText(FindRepositoryFile(relativePath));

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
