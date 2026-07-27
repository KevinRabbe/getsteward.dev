using SharedWorlds.Backend.Identity;
using SharedWorlds.Core.Domain;

namespace SharedWorlds.Backend.Worlds;

public sealed record SharedWorldPlayerPresence(
    WorldId WorldId,
    ExternalIdentityRef Player,
    string InstallationId,
    DateTimeOffset UpdatedAt);

public sealed record SharedWorldPlayerPresenceOptions
{
    public SharedWorldPlayerPresenceOptions(TimeSpan expiresAfter)
    {
        if (expiresAfter <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(expiresAfter));
        }

        ExpiresAfter = expiresAfter;
    }

    public TimeSpan ExpiresAfter { get; }

    public static SharedWorldPlayerPresenceOptions FirstReleaseDefaults { get; } = new(
        TimeSpan.FromSeconds(45));
}

public enum PublishSharedWorldPlayerPresenceStatus
{
    Published,
    NotFoundOrUnauthorized
}

public interface ISharedWorldPlayerPresenceStore
{
    Task UpsertAsync(
        SharedWorldPlayerPresence presence,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<SharedWorldPlayerPresence>> ListRecentAsync(
        WorldId worldId,
        DateTimeOffset cutoff,
        CancellationToken cancellationToken = default);

    Task<bool> DeleteAsync(
        WorldId worldId,
        ExternalIdentityRef player,
        string installationId,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Short-lived presentation evidence that an authenticated World member is currently inside a
/// Steward-observed Join session. It is deliberately non-authoritative: it cannot acquire or release
/// writable authority, grant access, decide who is Host, or advance World state.
/// </summary>
public sealed class SharedWorldPlayerPresenceService
{
    private readonly SharedWorldAccessService _access;
    private readonly ISharedWorldPlayerPresenceStore _store;
    private readonly Func<DateTimeOffset> _clock;
    private readonly SharedWorldPlayerPresenceOptions _options;

    public SharedWorldPlayerPresenceService(
        SharedWorldAccessService access,
        ISharedWorldPlayerPresenceStore store,
        Func<DateTimeOffset> clock,
        SharedWorldPlayerPresenceOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(access);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(clock);
        _access = access;
        _store = store;
        _clock = clock;
        _options = options ?? SharedWorldPlayerPresenceOptions.FirstReleaseDefaults;
    }

    public async Task<PublishSharedWorldPlayerPresenceStatus> PublishAsync(
        VerifiedExternalIdentity caller,
        string callerInstallationId,
        WorldId worldId,
        CancellationToken cancellationToken = default)
    {
        ValidateIdentity(caller);
        ValidateInstallationId(callerInstallationId);
        ValidateWorldId(worldId);

        var members = await _access.ListMembersAsync(caller, worldId, cancellationToken);
        if (members is null)
        {
            return PublishSharedWorldPlayerPresenceStatus.NotFoundOrUnauthorized;
        }

        await _store.UpsertAsync(
            new SharedWorldPlayerPresence(
                worldId,
                caller.Subject,
                callerInstallationId,
                _clock()),
            cancellationToken);
        return PublishSharedWorldPlayerPresenceStatus.Published;
    }

    public async Task<IReadOnlyList<SharedWorldPlayerPresence>?> ListVisibleAsync(
        VerifiedExternalIdentity caller,
        WorldId worldId,
        CancellationToken cancellationToken = default)
    {
        ValidateIdentity(caller);
        ValidateWorldId(worldId);

        var members = await _access.ListMembersAsync(caller, worldId, cancellationToken);
        if (members is null)
        {
            return null;
        }

        var activeMembers = members
            .Where(static member => member.Status == SharedWorldMemberStatus.Active)
            .Select(static member => member.Identity)
            .ToHashSet();
        if (activeMembers.Count == 0)
        {
            return Array.Empty<SharedWorldPlayerPresence>();
        }

        var recent = await _store.ListRecentAsync(
            worldId,
            _clock() - _options.ExpiresAfter,
            cancellationToken);

        return recent
            .Where(presence => activeMembers.Contains(presence.Player))
            .OrderByDescending(static presence => presence.UpdatedAt)
            .GroupBy(static presence => presence.Player)
            .Select(static group => group.First())
            .OrderBy(static presence => presence.Player.Provider, StringComparer.Ordinal)
            .ThenBy(static presence => presence.Player.ExternalId, StringComparer.Ordinal)
            .ToArray();
    }

    public async Task<bool> ClearAsync(
        VerifiedExternalIdentity caller,
        string callerInstallationId,
        WorldId worldId,
        CancellationToken cancellationToken = default)
    {
        ValidateIdentity(caller);
        ValidateInstallationId(callerInstallationId);
        ValidateWorldId(worldId);

        var members = await _access.ListMembersAsync(caller, worldId, cancellationToken);
        if (members is null)
        {
            return false;
        }

        return await _store.DeleteAsync(
            worldId,
            caller.Subject,
            callerInstallationId,
            cancellationToken);
    }

    private static void ValidateIdentity(VerifiedExternalIdentity caller)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentException.ThrowIfNullOrWhiteSpace(caller.Subject.Provider);
        ArgumentException.ThrowIfNullOrWhiteSpace(caller.Subject.ExternalId);
    }

    private static void ValidateInstallationId(string installationId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(installationId);
        if (installationId.Length > 256 || installationId.Any(char.IsControl))
        {
            throw new ArgumentException("Installation ID is invalid.", nameof(installationId));
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
