using SharedWorlds.Backend.Identity;
using SharedWorlds.Core.Domain;

namespace SharedWorlds.Backend.Worlds;

public enum CreateSharedWorldStatus
{
    Created,
    AlreadyExists
}

public sealed record CreateSharedWorldCommand(
    WorldId WorldId,
    string AdapterId,
    string DisplayName,
    RevisionId CurrentStateRevisionId,
    RevisionId? CurrentEnvironmentRevisionId);

public sealed record CreateSharedWorldResult(
    CreateSharedWorldStatus Status,
    SharedWorldMetadata? World);

/// <summary>
/// BE-2 application boundary for shared World metadata and access authorization.
/// Authentication proof verification happens before this service is called; this service accepts
/// only VerifiedExternalIdentity and never trusts caller-supplied identity strings.
/// </summary>
public sealed class SharedWorldMetadataService
{
    private readonly ISharedWorldMetadataStore _store;
    private readonly Func<DateTimeOffset> _utcNow;

    public SharedWorldMetadataService(
        ISharedWorldMetadataStore store,
        Func<DateTimeOffset> utcNow)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(utcNow);
        _store = store;
        _utcNow = utcNow;
    }

    public async Task<CreateSharedWorldResult> CreateSharedWorldAsync(
        VerifiedExternalIdentity caller,
        CreateSharedWorldCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(command);
        ValidateCommand(command);

        var now = _utcNow();
        var world = new SharedWorldMetadata(
            command.WorldId,
            command.AdapterId,
            command.DisplayName,
            command.CurrentStateRevisionId,
            command.CurrentEnvironmentRevisionId,
            caller.Subject,
            now,
            now);
        var managerMembership = new SharedWorldMember(
            command.WorldId,
            caller.Subject,
            SharedWorldMemberStatus.Active,
            now);

        var created = await _store.TryCreateWorldWithManagerAsync(
            world,
            managerMembership,
            cancellationToken);

        return created
            ? new(CreateSharedWorldStatus.Created, world)
            : new(CreateSharedWorldStatus.AlreadyExists, null);
    }

    public async Task<SharedWorldMetadata?> GetAccessibleWorldAsync(
        VerifiedExternalIdentity caller,
        WorldId worldId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ValidateWorldId(worldId);

        var membership = await _store.LoadMemberAsync(worldId, caller.Subject, cancellationToken);
        if (membership?.Status != SharedWorldMemberStatus.Active)
        {
            // Do not expose whether the World exists to a non-member.
            return null;
        }

        return await _store.LoadWorldAsync(worldId, cancellationToken);
    }

    public Task<IReadOnlyList<SharedWorldMetadata>> ListAccessibleWorldsAsync(
        VerifiedExternalIdentity caller,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(caller);
        return _store.ListWorldsForActiveMemberAsync(caller.Subject, cancellationToken);
    }

    private static void ValidateCommand(CreateSharedWorldCommand command)
    {
        ValidateWorldId(command.WorldId);
        ArgumentException.ThrowIfNullOrWhiteSpace(command.AdapterId);
        ArgumentException.ThrowIfNullOrWhiteSpace(command.DisplayName);

        if (command.CurrentStateRevisionId.Value == Guid.Empty)
        {
            throw new ArgumentException(
                "Current state revision is required.",
                nameof(command));
        }

        if (command.CurrentEnvironmentRevisionId is { Value: var environmentValue } &&
            environmentValue == Guid.Empty)
        {
            throw new ArgumentException(
                "Environment revision cannot be empty when provided.",
                nameof(command));
        }
    }

    private static void ValidateWorldId(WorldId worldId)
    {
        if (worldId.Value == Guid.Empty)
        {
            throw new ArgumentException("World ID is required.", nameof(worldId));
        }
    }
}
