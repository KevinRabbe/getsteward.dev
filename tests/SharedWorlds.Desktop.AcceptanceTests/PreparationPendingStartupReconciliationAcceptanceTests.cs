using Xunit;

namespace SharedWorlds.Desktop.AcceptanceTests;

public sealed class PreparationPendingStartupReconciliationAcceptanceTests
{
    [Fact]
    public void StartupReconcilesOnlyExactManagedPreparationBeforeResponsibilityProjection()
    {
        var source = File.ReadAllText(FindRepositoryFile(
            "src/SharedWorlds.Desktop/MainWindow.Tray.cs"));
        var start = source.IndexOf(
            "private async Task InitializeRuntimeResponsibilityAsync()",
            StringComparison.Ordinal);
        var end = source.IndexOf(
            "private void OnLifecyclePhaseChanged",
            start,
            StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start);
        var body = source[start..end];

        Assert.Contains(
            "record.Status == WorkspaceRecoveryStatus.PreparationPending",
            body,
            StringComparison.Ordinal);
        Assert.Contains(
            "PreparedWorldRecoveryLocationKind.SafeWorldManaged",
            body,
            StringComparison.Ordinal);
        Assert.Contains(
            "await reconciliation.ResolveManagedAsync(record.Id);",
            body,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "ResolveManagedAsync(record.WorldId)",
            body,
            StringComparison.Ordinal);
        Assert.Contains(
            "records = await _workspaceRecoveryStore.ListAsync();",
            body,
            StringComparison.Ordinal);

        var resolve = body.IndexOf(
            "await reconciliation.ResolveManagedAsync(record.Id);",
            StringComparison.Ordinal);
        var projection = body.IndexOf(
            "_responsibilityTracker.InitializeFromRecoveryRecords(records);",
            StringComparison.Ordinal);
        Assert.True(resolve >= 0 && projection > resolve);
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
