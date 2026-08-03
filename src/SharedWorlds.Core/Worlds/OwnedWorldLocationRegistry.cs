using SharedWorlds.Core.Domain;

namespace SharedWorlds.Core.Worlds;

public sealed record OwnedInstallationRegistration(
    string InstallationId,
    string OwnerProvider,
    string OwnerExternalId,
    string DisplayName,
    DateTimeOffset RegisteredAt,
    DateTimeOffset LastSeenAt);

public enum OwnedWorldLocationWriteResult
{
    Created,
    Updated,
    NoChange,
    Conflict
}

public sealed record OwnedWorldLocationWriteDecision(
    OwnedWorldLocationWriteResult Result,
    OwnedWorldLocationClaim? Current,
    string Reason);

/// <summary>
/// Durable persistence boundary for private owned-installation World locations. Implementations must
/// make installation registration and location compare-and-swap writes atomic at their own storage
/// boundary. Visibility alone never proves ownership; every query and mutation is owner-scoped.
/// </summary>
public interface IOwnedWorldLocationStore
{
    Task<OwnedInstallationRegistration?> GetInstallationAsync(
        string installationId,
        CancellationToken cancellationToken = default);

    Task RegisterInstallationAsync(
        OwnedInstallationRegistration registration,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<OwnedWorldLocationClaim>> ListWorldLocationsAsync(
        WorldId worldId,
        string ownerProvider,
        string ownerExternalId,
        CancellationToken cancellationToken = default);

    Task<OwnedWorldLocationWriteDecision> CompareExchangeLocationAsync(
        OwnedWorldLocationClaim desired,
        RevisionId? expectedStateRevisionId,
        RevisionId? expectedEnvironmentRevisionId,
        CancellationToken cancellationToken = default);

    Task<OwnedWorldLocationWriteDecision> RemoveLocationAsync(
        WorldId worldId,
        string ownerProvider,
        string ownerExternalId,
        string installationId,
        RevisionId expectedStateRevisionId,
        RevisionId expectedEnvironmentRevisionId,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Validates authenticated owner/installations before delegating to the atomic persistence boundary.
/// The service never applies timestamp-based last-write-wins behavior.
/// </summary>
public sealed class OwnedWorldLocationRegistry
{
    private const int MaximumInstallationIdLength = 128;
    private const int MaximumDisplayNameLength = 80;

    private readonly IOwnedWorldLocationStore _store;

    public OwnedWorldLocationRegistry(IOwnedWorldLocationStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        _store = store;
    }

    public async Task<OwnedInstallationRegistration> RegisterInstallationAsync(
        UserIdentity authenticatedOwner,
        string installationId,
        string displayName,
        DateTimeOffset observedAt,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(authenticatedOwner);
        ValidateBoundedText(installationId, nameof(installationId), MaximumInstallationIdLength);
        ValidateBoundedText(displayName, nameof(displayName), MaximumDisplayNameLength);

        var existing = await _store.GetInstallationAsync(installationId, cancellationToken);
        if (existing is not null && !SameOwner(existing, authenticatedOwner))
        {
            throw new InvalidOperationException(
                "This installation ID is already registered to a different authenticated owner.");
        }

        var registration = existing is null
            ? new OwnedInstallationRegistration(
                installationId,
                authenticatedOwner.Provider,
                authenticatedOwner.ExternalId,
                displayName,
                observedAt,
                observedAt)
            : existing with
            {
                DisplayName = displayName,
                LastSeenAt = observedAt
            };

        await _store.RegisterInstallationAsync(registration, cancellationToken);
        return registration;
    }

    public async Task<OwnedWorldLocationWriteDecision> PublishLocationAsync(
        UserIdentity authenticatedOwner,
        string installationId,
        WorldId worldId,
        RevisionId stateRevisionId,
        RevisionId environmentRevisionId,
        DateTimeOffset observedAt,
        RevisionId? expectedStateRevisionId = null,
        RevisionId? expectedEnvironmentRevisionId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(authenticatedOwner);
        ValidateBoundedText(installationId, nameof(installationId), MaximumInstallationIdLength);
        EnsureExpectedPair(expectedStateRevisionId, expectedEnvironmentRevisionId);

        var installation = await _store.GetInstallationAsync(installationId, cancellationToken)
            ?? throw new InvalidOperationException(
                "The installation must be registered before it can publish a private World location.");
        if (!SameOwner(installation, authenticatedOwner))
        {
            throw new InvalidOperationException(
                "The authenticated owner does not own this installation registration.");
        }

        var desired = new OwnedWorldLocationClaim(
            worldId,
            authenticatedOwner.Provider,
            authenticatedOwner.ExternalId,
            installationId,
            stateRevisionId,
            environmentRevisionId,
            observedAt);

        return await _store.CompareExchangeLocationAsync(
            desired,
            expectedStateRevisionId,
            expectedEnvironmentRevisionId,
            cancellationToken);
    }

    public async Task<OwnedWorldLocationWriteDecision> RemoveLocationAsync(
        UserIdentity authenticatedOwner,
        string installationId,
        WorldId worldId,
        RevisionId expectedStateRevisionId,
        RevisionId expectedEnvironmentRevisionId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(authenticatedOwner);
        ValidateBoundedText(installationId, nameof(installationId), MaximumInstallationIdLength);

        var installation = await _store.GetInstallationAsync(installationId, cancellationToken)
            ?? throw new InvalidOperationException(
                "The installation is not registered.");
        if (!SameOwner(installation, authenticatedOwner))
        {
            throw new InvalidOperationException(
                "The authenticated owner does not own this installation registration.");
        }

        return await _store.RemoveLocationAsync(
            worldId,
            authenticatedOwner.Provider,
            authenticatedOwner.ExternalId,
            installationId,
            expectedStateRevisionId,
            expectedEnvironmentRevisionId,
            cancellationToken);
    }

    private static void EnsureExpectedPair(
        RevisionId? expectedStateRevisionId,
        RevisionId? expectedEnvironmentRevisionId)
    {
        if (expectedStateRevisionId.HasValue != expectedEnvironmentRevisionId.HasValue)
        {
            throw new ArgumentException(
                "Expected state and environment revisions must either both be supplied or both be absent.");
        }
    }

    private static bool SameOwner(
        OwnedInstallationRegistration registration,
        UserIdentity owner)
        => string.Equals(registration.OwnerProvider, owner.Provider, StringComparison.Ordinal) &&
           string.Equals(registration.OwnerExternalId, owner.ExternalId, StringComparison.Ordinal);

    private static void ValidateBoundedText(string value, string parameterName, int maximumLength)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        if (value.Length > maximumLength)
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                $"Value must not exceed {maximumLength} characters.");
        }

        if (value.Any(char.IsControl))
        {
            throw new ArgumentException("Control characters are not allowed.", parameterName);
        }
    }
}
