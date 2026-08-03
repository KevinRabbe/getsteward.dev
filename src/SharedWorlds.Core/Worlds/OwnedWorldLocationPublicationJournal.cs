using SharedWorlds.Core.Domain;

namespace SharedWorlds.Core.Worlds;

public enum OwnedWorldLocationPublicationOperationKind
{
    Publish,
    Remove
}

/// <summary>
/// One crash-replayable remote compare-and-swap operation. The operation is persisted before network
/// access and remains unchanged until the exact outcome is acknowledged locally.
/// </summary>
public sealed record OwnedWorldLocationPublicationOperation(
    OwnedWorldLocationPublicationOperationKind Kind,
    RevisionId? StateRevisionId,
    RevisionId? EnvironmentRevisionId,
    RevisionId? ExpectedStateRevisionId,
    RevisionId? ExpectedEnvironmentRevisionId,
    OwnedWorldPresentation? Presentation = null)
{
    public static OwnedWorldLocationPublicationOperation Publish(
        RevisionId stateRevisionId,
        RevisionId environmentRevisionId,
        RevisionId? expectedStateRevisionId = null,
        RevisionId? expectedEnvironmentRevisionId = null,
        OwnedWorldPresentation? presentation = null)
    {
        EnsurePair(
            expectedStateRevisionId,
            expectedEnvironmentRevisionId,
            "Expected state and environment revisions");
        presentation?.Validate();
        return new(
            OwnedWorldLocationPublicationOperationKind.Publish,
            stateRevisionId,
            environmentRevisionId,
            expectedStateRevisionId,
            expectedEnvironmentRevisionId,
            presentation);
    }

    public static OwnedWorldLocationPublicationOperation Remove(
        RevisionId expectedStateRevisionId,
        RevisionId expectedEnvironmentRevisionId)
        => new(
            Kind: OwnedWorldLocationPublicationOperationKind.Remove,
            StateRevisionId: null,
            EnvironmentRevisionId: null,
            ExpectedStateRevisionId: expectedStateRevisionId,
            ExpectedEnvironmentRevisionId: expectedEnvironmentRevisionId,
            Presentation: null);

    public void Validate()
    {
        EnsurePair(
            ExpectedStateRevisionId,
            ExpectedEnvironmentRevisionId,
            "Expected state and environment revisions");
        EnsureNonEmpty(StateRevisionId, "State revision");
        EnsureNonEmpty(EnvironmentRevisionId, "Environment revision");
        EnsureNonEmpty(ExpectedStateRevisionId, "Expected state revision");
        EnsureNonEmpty(ExpectedEnvironmentRevisionId, "Expected environment revision");
        Presentation?.Validate();

        switch (Kind)
        {
            case OwnedWorldLocationPublicationOperationKind.Publish:
                if (StateRevisionId is null || EnvironmentRevisionId is null)
                {
                    throw new InvalidDataException(
                        "A publish operation requires exact state and environment revisions.");
                }

                break;

            case OwnedWorldLocationPublicationOperationKind.Remove:
                if (StateRevisionId is not null ||
                    EnvironmentRevisionId is not null ||
                    Presentation is not null)
                {
                    throw new InvalidDataException(
                        "A remove operation cannot contain a desired state/environment head or presentation.");
                }

                if (ExpectedStateRevisionId is null || ExpectedEnvironmentRevisionId is null)
                {
                    throw new InvalidDataException(
                        "A remove operation requires the exact previously observed state/environment head.");
                }

                break;

            default:
                throw new InvalidDataException(
                    $"Unsupported owned-World location publication operation '{Kind}'.");
        }
    }

    private static void EnsurePair(
        RevisionId? stateRevisionId,
        RevisionId? environmentRevisionId,
        string name)
    {
        if (stateRevisionId.HasValue != environmentRevisionId.HasValue)
        {
            throw new InvalidDataException($"{name} must both be present or both be absent.");
        }
    }

    private static void EnsureNonEmpty(RevisionId? revisionId, string name)
    {
        if (revisionId.HasValue && revisionId.Value.Value == Guid.Empty)
        {
            throw new InvalidDataException($"{name} must not be empty.");
        }
    }
}

/// <summary>
/// Durable, bounded local publication state for one private World. The exact durable installation ID
/// is part of the authority record because every backend CAS is installation-scoped. Desired is the
/// newest local canonical head and its current bounded presentation (or absent after local removal).
/// Confirmed is the last backend result acknowledged locally. InFlight is at most one immutable CAS
/// operation and is never replaced by later local head or presentation changes until reconciled.
/// </summary>
public sealed record OwnedWorldLocationPublicationState(
    WorldId WorldId,
    string InstallationId,
    RevisionId? DesiredStateRevisionId,
    RevisionId? DesiredEnvironmentRevisionId,
    RevisionId? ConfirmedStateRevisionId,
    RevisionId? ConfirmedEnvironmentRevisionId,
    OwnedWorldLocationPublicationOperation? InFlight,
    DateTimeOffset UpdatedAt,
    OwnedWorldPresentation? DesiredPresentation = null,
    OwnedWorldPresentation? ConfirmedPresentation = null)
{
    private const int MaximumInstallationIdLength = 128;

    public bool RemovalRequested => DesiredStateRevisionId is null;

    public bool IsSynchronized =>
        InFlight is null &&
        DesiredStateRevisionId == ConfirmedStateRevisionId &&
        DesiredEnvironmentRevisionId == ConfirmedEnvironmentRevisionId &&
        DesiredPresentation == ConfirmedPresentation;

    public void Validate()
    {
        if (WorldId.Value == Guid.Empty)
        {
            throw new InvalidDataException("Publication state requires a non-empty World ID.");
        }

        ValidateInstallationId(InstallationId);
        EnsurePair(
            DesiredStateRevisionId,
            DesiredEnvironmentRevisionId,
            "Desired state and environment revisions");
        EnsurePair(
            ConfirmedStateRevisionId,
            ConfirmedEnvironmentRevisionId,
            "Confirmed state and environment revisions");
        EnsureNonEmpty(DesiredStateRevisionId, "Desired state revision");
        EnsureNonEmpty(DesiredEnvironmentRevisionId, "Desired environment revision");
        EnsureNonEmpty(ConfirmedStateRevisionId, "Confirmed state revision");
        EnsureNonEmpty(ConfirmedEnvironmentRevisionId, "Confirmed environment revision");
        DesiredPresentation?.Validate();
        ConfirmedPresentation?.Validate();

        if (DesiredStateRevisionId is null && DesiredPresentation is not null)
        {
            throw new InvalidDataException(
                "A removal request cannot contain desired World presentation.");
        }

        if (ConfirmedStateRevisionId is null && ConfirmedPresentation is not null)
        {
            throw new InvalidDataException(
                "Confirmed World presentation requires a confirmed state/environment head.");
        }

        if (UpdatedAt == default)
        {
            throw new InvalidDataException("Publication state requires an update timestamp.");
        }

        InFlight?.Validate();
        if (InFlight is not null &&
            (InFlight.ExpectedStateRevisionId != ConfirmedStateRevisionId ||
             InFlight.ExpectedEnvironmentRevisionId != ConfirmedEnvironmentRevisionId))
        {
            throw new InvalidDataException(
                "The in-flight CAS expectation must equal the last locally confirmed backend head.");
        }

        if (DesiredStateRevisionId is null &&
            ConfirmedStateRevisionId is null &&
            InFlight is null)
        {
            throw new InvalidDataException(
                "An empty owned-World location publication state has no durable work or evidence.");
        }
    }

    public static void ValidateInstallationId(string installationId)
    {
        if (string.IsNullOrWhiteSpace(installationId))
        {
            throw new InvalidDataException(
                "Publication state requires a durable installation ID.");
        }

        if (installationId.Length > MaximumInstallationIdLength)
        {
            throw new InvalidDataException(
                $"Publication-state installation ID must not exceed {MaximumInstallationIdLength} characters.");
        }

        if (installationId.Any(char.IsControl))
        {
            throw new InvalidDataException(
                "Publication-state installation ID cannot contain control characters.");
        }
    }

    private static void EnsurePair(
        RevisionId? stateRevisionId,
        RevisionId? environmentRevisionId,
        string name)
    {
        if (stateRevisionId.HasValue != environmentRevisionId.HasValue)
        {
            throw new InvalidDataException($"{name} must both be present or both be absent.");
        }
    }

    private static void EnsureNonEmpty(RevisionId? revisionId, string name)
    {
        if (revisionId.HasValue && revisionId.Value.Value == Guid.Empty)
        {
            throw new InvalidDataException($"{name} must not be empty.");
        }
    }
}

/// <summary>
/// Durable local write-ahead boundary for private owned-World location publication. Implementations
/// must atomically replace one World entry and fail closed on malformed or mismatched persisted data.
/// </summary>
public interface IOwnedWorldLocationPublicationJournal
{
    Task SaveAsync(
        OwnedWorldLocationPublicationState state,
        CancellationToken cancellationToken = default);

    Task<OwnedWorldLocationPublicationState?> LoadAsync(
        WorldId worldId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<OwnedWorldLocationPublicationState>> ListAsync(
        CancellationToken cancellationToken = default);

    Task RemoveAsync(
        WorldId worldId,
        CancellationToken cancellationToken = default);
}
