using Xunit;

namespace SharedWorlds.Desktop.AcceptanceTests;

public sealed class WorldHistoryStorageSizePresentationTests
{
    [Fact]
    public void StorageConfirmationUsesPlannedBytesAndResultUsesActualBytes()
    {
        var history = File.ReadAllText(FindRepositoryFile(
            "src/SharedWorlds.Desktop/MainWindow.WorldHistory.cs"));

        Assert.Contains("FormatStorageSize(plan.PlannedReclaimableBytes)", history, StringComparison.Ordinal);
        Assert.Contains("Reclaim {plannedSize} from {plan.EvictionCandidates.Count}", history, StringComparison.Ordinal);
        Assert.Contains("FormatStorageSize(result.ReclaimedBytes)", history, StringComparison.Ordinal);
        Assert.Contains("Reclaimed {reclaimedSize} from {result.EvictedPayloads}", history, StringComparison.Ordinal);
        Assert.Contains("[\"B\", \"KiB\", \"MiB\", \"GiB\", \"TiB\"]", history, StringComparison.Ordinal);
        Assert.Contains("CultureInfo.CurrentCulture", history, StringComparison.Ordinal);
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
