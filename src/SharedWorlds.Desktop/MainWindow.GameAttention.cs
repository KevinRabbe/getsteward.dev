using SharedWorlds.Core.Worlds;

namespace SharedWorlds.Desktop;

public partial class MainWindow
{
    /// <summary>
    /// Projects the already-authoritative one-device responsibility snapshot into the Games Library.
    /// This owns no lifecycle state: when the tracker changes, the tile summary is rebuilt from the
    /// current World list and disappears automatically when responsibility becomes None.
    /// </summary>
    private void UpdateGameLibraryResponsibilityAttention()
    {
        if (GameLibraryList.ItemsSource is not IEnumerable<GameLibraryItem> source)
        {
            return;
        }

        var current = source.ToArray();
        if (current.Length == 0)
        {
            return;
        }

        var snapshot = _responsibilityTracker.Current;
        var attention = FormatGameLibraryAttention(snapshot);
        string? attentionAdapterId = null;
        if (attention is not null && snapshot.WorldId is { } worldId)
        {
            attentionAdapterId = _allWorldItems
                .FirstOrDefault(item => item.World.Id == worldId)
                ?.AdapterId;
        }

        var updated = current
            .Select(item => item with
            {
                Summary = BuildGameLibrarySummary(
                    item.AdapterId,
                    attentionAdapterId is not null &&
                    string.Equals(item.AdapterId, attentionAdapterId, StringComparison.Ordinal)
                        ? attention
                        : null)
            })
            .ToArray();

        if (current.SequenceEqual(updated))
        {
            return;
        }

        GameLibraryList.ItemsSource = updated;
    }

    private string BuildGameLibrarySummary(string adapterId, string? attention)
    {
        var worldCount = _allWorldItems.Count(item =>
            string.Equals(item.AdapterId, adapterId, StringComparison.Ordinal));
        var countSummary = worldCount switch
        {
            0 => "No managed Worlds yet",
            1 => "1 managed World",
            _ => $"{worldCount} managed Worlds"
        };

        return string.IsNullOrWhiteSpace(attention)
            ? countSummary
            : $"{countSummary}  •  {attention}";
    }

    private static string? FormatGameLibraryAttention(WorldLifecycleResponsibilitySnapshot snapshot)
        => snapshot.Kind switch
        {
            WorldLifecycleResponsibilityKind.None => null,
            WorldLifecycleResponsibilityKind.InterruptedSession => "Recovery needed",
            WorldLifecycleResponsibilityKind.RecoveryNeeded => "Recovery needed",
            WorldLifecycleResponsibilityKind.CleanupPending => "Action required",
            WorldLifecycleResponsibilityKind.ActiveLifecycle => snapshot.Phase switch
            {
                WorldLifecyclePhase.Running => snapshot.Mode == ManagedWorldSessionMode.Hosted
                    ? "Hosting"
                    : "Running",
                WorldLifecyclePhase.WaitingForSafeCapture or
                WorldLifecyclePhase.Capturing or
                WorldLifecyclePhase.StoringCandidate or
                WorldLifecyclePhase.Committing or
                WorldLifecyclePhase.Finalizing => "Saving World",
                _ => "Preparing"
            },
            _ => null
        };
}
