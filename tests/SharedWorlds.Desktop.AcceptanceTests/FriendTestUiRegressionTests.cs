using Xunit;

namespace SharedWorlds.Desktop.AcceptanceTests;

public sealed class FriendTestUiRegressionTests
{
    [Fact]
    public void HostedRunningPhaseReleasesOnlyPresentationBusyWhileCoreResponsibilityRemainsActive()
    {
        var tray = File.ReadAllText(FindRepositoryFile(
            "src/SharedWorlds.Desktop/MainWindow.Tray.cs"));
        var lifecycle = File.ReadAllText(FindRepositoryFile(
            "src/SharedWorlds.Core/Worlds/WorldLifecycleService.cs"));
        var responsibility = File.ReadAllText(FindRepositoryFile(
            "src/SharedWorlds.Core/Worlds/WorldLifecycleResponsibilityTracker.cs"));

        Assert.Contains("ApplyHostedLifecycleShellState(change);", tray, StringComparison.Ordinal);
        Assert.Contains("change.Mode != ManagedWorldSessionMode.Hosted", tray, StringComparison.Ordinal);
        Assert.Contains("case WorldLifecyclePhase.Running:", tray, StringComparison.Ordinal);
        Assert.Contains("SetBusy(false);", tray, StringComparison.Ordinal);
        Assert.Contains("case WorldLifecyclePhase.WaitingForSafeCapture:", tray, StringComparison.Ordinal);
        Assert.Contains("case WorldLifecyclePhase.Committing:", tray, StringComparison.Ordinal);
        Assert.Contains("SetBusy(true);", tray, StringComparison.Ordinal);

        var registerHost = lifecycle.IndexOf("_activeHostedSessions.TryAdd(worldId, activeHostedSession)", StringComparison.Ordinal);
        var running = lifecycle.IndexOf("Notify(worldId, mode, WorldLifecyclePhase.Running);", StringComparison.Ordinal);
        var wait = lifecycle.IndexOf("await adapter.WaitForSessionEndAsync(session, cancellationToken);", StringComparison.Ordinal);
        Assert.True(registerHost >= 0 && running > registerHost && wait > running);

        Assert.Contains("_kind = WorldLifecycleResponsibilityKind.ActiveLifecycle;", responsibility, StringComparison.Ordinal);
        Assert.Contains("_mode = change.Mode;", responsibility, StringComparison.Ordinal);
    }

    [Fact]
    public void VerificationIsRetainedPerExactWorldEnvironmentRevisionAcrossPresentationRefreshes()
    {
        var readiness = File.ReadAllText(FindRepositoryFile(
            "src/SharedWorlds.Desktop/MainWindow.EnvironmentReadiness.cs"));
        var lifecycle = File.ReadAllText(FindRepositoryFile(
            "src/SharedWorlds.Core/Worlds/WorldLifecycleService.cs"));

        Assert.Contains(
            "Dictionary<EnvironmentVerificationKey, EnvironmentVerificationReport>",
            readiness,
            StringComparison.Ordinal);
        Assert.Contains(
            "_environmentVerifications.TryGetValue(",
            readiness,
            StringComparison.Ordinal);
        Assert.Contains(
            "_environmentVerifications[GetEnvironmentVerificationKey(world)] = verification;",
            readiness,
            StringComparison.Ordinal);
        Assert.Contains(
            "world.CurrentEnvironmentRevisionId",
            readiness,
            StringComparison.Ordinal);
        Assert.Contains(
            "private readonly record struct EnvironmentVerificationKey(",
            readiness,
            StringComparison.Ordinal);

        var resetStart = readiness.IndexOf("private void ResetEnvironmentReadinessUi()", StringComparison.Ordinal);
        var updateStart = readiness.IndexOf("private void UpdateEnvironmentReadinessUi()", StringComparison.Ordinal);
        Assert.True(resetStart >= 0 && updateStart > resetStart);
        var resetBody = readiness[resetStart..updateStart];
        Assert.DoesNotContain("_environmentVerifications.Clear", resetBody, StringComparison.Ordinal);
        Assert.DoesNotContain("= null", resetBody, StringComparison.Ordinal);
        Assert.Contains("UpdateEnvironmentReadinessUi();", resetBody, StringComparison.Ordinal);

        // The retained UI result is convenience, not authority. Core still verifies the exact
        // canonical environment again before any shared writable session starts.
        Assert.Contains("if (world.SharingMode == WorldSharingMode.Shared)", lifecycle, StringComparison.Ordinal);
        Assert.Contains("await adapter.VerifyEnvironmentAsync(", lifecycle, StringComparison.Ordinal);
        Assert.Contains("if (!verification.IsReady)", lifecycle, StringComparison.Ordinal);
        Assert.Contains("throw new EnvironmentReproductionException(", lifecycle, StringComparison.Ordinal);
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
