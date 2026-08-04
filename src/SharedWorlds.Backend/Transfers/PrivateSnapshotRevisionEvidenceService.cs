using SharedWorlds.Backend.Identity;
using SharedWorlds.Core.Domain;
using SharedWorlds.Core.Worlds;

namespace SharedWorlds.Backend.Transfers;

public sealed record PublishPrivateSnapshotRevisionEvidenceCommand(
    WorldId WorldId,
    StateRevision StateRevision,
    EnvironmentRevision EnvironmentRevision);

public enum PublishPrivateSnapshotRevisionEvidenceStatus
{
    Published,
    AlreadyPublished,
    NotFoundOrUnauthorized,
    InvalidRequest,
    Conflict
}

public sealed record PublishPrivateSnapshotRevisionEvidenceResult(
    PublishPrivateSnapshotRevisionEvidenceStatus Status,
    OwnedWorldSnapshotRevisionEvidence? Evidence,
    string Reason);

/// <summary>
/// Authenticated source-side authority for publishing the exact immutable revision records behind an
/// already verified owner-private snapshot. Owner, installation, and evidence time are backend-derived;
/// a client cannot publish evidence for another source key or predeclare a server timestamp.
/// </summary>
public sealed class PrivateSnapshotRevisionEvidenceService
{
    private readonly IOwnedWorldSnapshotStore _snapshots;
    private readonly IOwnedWorldSnapshotRevisionEvidenceStore _revisionEvidence;
    private readonly Func<DateTimeOffset> _utcNow;

    public PrivateSnapshotRevisionEvidenceService(
        IOwnedWorldSnapshotStore snapshots,
        IOwnedWorldSnapshotRevisionEvidenceStore revisionEvidence,
        Func<DateTimeOffset> utcNow)
    {
        ArgumentNullException.ThrowIfNull(snapshots);
        ArgumentNullException.ThrowIfNull(revisionEvidence);
        ArgumentNullException.ThrowIfNull(utcNow);
        _snapshots = snapshots;
        _revisionEvidence = revisionEvidence;
        _utcNow = utcNow;
    }

    public async Task<PublishPrivateSnapshotRevisionEvidenceResult> PublishAsync(
        StewardAuthenticatedCaller caller,
        PublishPrivateSnapshotRevisionEvidenceCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(command);

        var owner = ToUserIdentity(caller.Identity);
        OwnedWorldSnapshotRevisionEvidence evidence;
        try
        {
            evidence = new OwnedWorldSnapshotRevisionEvidence(
                owner.Provider,
                owner.ExternalId,
                caller.InstallationId,
                command.WorldId,
                command.StateRevision,
                command.EnvironmentRevision,
                _utcNow());
            evidence.Validate();
        }
        catch (InvalidDataException exception)
        {
            return new(
                PublishPrivateSnapshotRevisionEvidenceStatus.InvalidRequest,
                Evidence: null,
                exception.Message);
        }

        var snapshot = await _snapshots.LoadExactAsync(
            owner.Provider,
            owner.ExternalId,
            command.WorldId,
            caller.InstallationId,
            command.StateRevision.Id,
            command.EnvironmentRevision.Id,
            cancellationToken);
        if (snapshot is null)
        {
            return new(
                PublishPrivateSnapshotRevisionEvidenceStatus.NotFoundOrUnauthorized,
                Evidence: null,
                "The private snapshot was not found or is not authorized.");
        }

        try
        {
            evidence.ValidateAgainst(snapshot);
        }
        catch (InvalidDataException exception)
        {
            return new(
                PublishPrivateSnapshotRevisionEvidenceStatus.Conflict,
                Evidence: null,
                exception.Message);
        }

        var publication = await _revisionEvidence.PublishAsync(
            evidence,
            cancellationToken);
        return publication.Result switch
        {
            OwnedWorldSnapshotRevisionEvidenceWriteResult.Created => new(
                PublishPrivateSnapshotRevisionEvidenceStatus.Published,
                publication.Current,
                publication.Reason),
            OwnedWorldSnapshotRevisionEvidenceWriteResult.NoChange => new(
                PublishPrivateSnapshotRevisionEvidenceStatus.AlreadyPublished,
                publication.Current,
                publication.Reason),
            OwnedWorldSnapshotRevisionEvidenceWriteResult.Conflict => new(
                PublishPrivateSnapshotRevisionEvidenceStatus.Conflict,
                publication.Current,
                publication.Reason),
            _ => throw new InvalidOperationException(
                $"Unsupported private revision evidence write result '{publication.Result}'.")
        };
    }

    private static UserIdentity ToUserIdentity(VerifiedExternalIdentity identity)
        => new(
            identity.Subject.Provider,
            identity.Subject.ExternalId,
            identity.DisplayName);
}
