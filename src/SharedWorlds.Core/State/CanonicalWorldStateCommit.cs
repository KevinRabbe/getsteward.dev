using SharedWorlds.Core.Abstractions;

namespace SharedWorlds.Core.State;

/// <summary>
/// The immutable canonical state currently selected for a World.
/// Runtime save directories are materializations of this revision, not competing authorities.
/// </summary>
public sealed record CanonicalWorldStateHead(
    string WorldId,
    string RevisionId,
    string PackagePath,
    string? ParentRevisionId,
    DateTimeOffset CapturedAt,
    DateTimeOffset CommittedAt);

public enum CanonicalWorldStateCommitStatus
{
    Committed,
    Unchanged,
    HeadChanged
}

public sealed record CanonicalWorldStateCommitResult(
    CanonicalWorldStateCommitStatus Status,
    CanonicalWorldStateHead? Head,
    string? ExpectedHeadRevisionId,
    string? ObservedHeadRevisionId)
{
    public bool Succeeded => Status is
        CanonicalWorldStateCommitStatus.Committed or
        CanonicalWorldStateCommitStatus.Unchanged;

    public bool AdvancedHead => Status == CanonicalWorldStateCommitStatus.Committed;
}

/// <summary>
/// Owns durable package storage and compare-and-swap advancement of a World's canonical head.
/// Implementations must not expose a new head until the complete candidate package is durable.
/// A HeadChanged result must leave the candidate package owned by the caller.
/// </summary>
public interface ICanonicalWorldStateStore
{
    Task<CanonicalWorldStateHead?> ReadHeadAsync(
        string worldId,
        CancellationToken cancellationToken = default);

    Task<CanonicalWorldStateCommitResult> TryCommitAsync(
        string worldId,
        string? expectedHeadRevisionId,
        CapturedState candidate,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Applies the session transaction boundary: the candidate may advance the World exactly when the
/// head still equals the revision from which the session started.
/// </summary>
public sealed class CanonicalWorldStateCommitCoordinator
{
    private readonly ICanonicalWorldStateStore _store;

    public CanonicalWorldStateCommitCoordinator(ICanonicalWorldStateStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    public Task<CanonicalWorldStateHead?> ReadHeadAsync(
        string worldId,
        CancellationToken cancellationToken = default)
    {
        ValidateWorldId(worldId);
        return _store.ReadHeadAsync(worldId, cancellationToken);
    }

    public Task<CanonicalWorldStateCommitResult> CommitAsync(
        string worldId,
        string? expectedHeadRevisionId,
        CapturedState candidate,
        CancellationToken cancellationToken = default)
    {
        ValidateWorldId(worldId);
        ArgumentNullException.ThrowIfNull(candidate);

        if (string.IsNullOrWhiteSpace(candidate.Package.Path))
        {
            throw new ArgumentException("The captured state package path is required.", nameof(candidate));
        }

        return _store.TryCommitAsync(
            worldId,
            NormalizeRevisionId(expectedHeadRevisionId),
            candidate,
            cancellationToken);
    }

    private static void ValidateWorldId(string worldId)
    {
        if (string.IsNullOrWhiteSpace(worldId))
        {
            throw new ArgumentException("World id is required.", nameof(worldId));
        }
    }

    private static string? NormalizeRevisionId(string? revisionId)
        => string.IsNullOrWhiteSpace(revisionId) ? null : revisionId.Trim();
}
