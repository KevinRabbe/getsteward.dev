using SharedWorlds.Core.Domain;

namespace SharedWorlds.Core.Worlds;

/// <summary>
/// Bounded private presentation metadata carried with a location claim so another installation can
/// identify the World before materialization. It is not canonical World state or content authority.
/// </summary>
public sealed record OwnedWorldPresentation(
    string Name,
    string GameAdapterId);

public sealed record OwnedWorldLocationClaim(
    WorldId WorldId,
    string OwnerProvider,
    string OwnerExternalId,
    string InstallationId,
    RevisionId StateRevisionId,
    RevisionId EnvironmentRevisionId,
    DateTimeOffset ObservedAt)
{
    /// <summary>
    /// Older persisted claims may not have presentation metadata. Such claims remain valid for exact
    /// known-World availability checks but are not sufficient for owner-private catalog discovery.
    /// </summary>
    public OwnedWorldPresentation? Presentation { get; init; }
}

public enum BringHereAvailability
{
    Unavailable,
    Available,
    AlreadyHere,
    Conflict
}

public sealed record BringHereAuthorityDecision(
    BringHereAvailability Availability,
    OwnedWorldLocationClaim? Source,
    IReadOnlyList<OwnedWorldLocationClaim> ConflictingClaims,
    string Reason);

/// <summary>
/// Resolves whether an authenticated owner may materialize a private World from another owned
/// installation. This service never guesses between divergent World heads. The backend remains
/// responsible for authenticating the user and proving that every supplied installation belongs to
/// that same identity.
/// </summary>
public sealed class BringHereAuthorityService
{
    public BringHereAuthorityDecision Resolve(
        WorldId worldId,
        UserIdentity authenticatedOwner,
        string targetInstallationId,
        IEnumerable<OwnedWorldLocationClaim> claims)
    {
        ArgumentNullException.ThrowIfNull(authenticatedOwner);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetInstallationId);
        ArgumentNullException.ThrowIfNull(claims);

        var ownedClaims = claims
            .Where(claim => claim.WorldId == worldId)
            .Where(claim => SameOwner(claim, authenticatedOwner))
            .OrderByDescending(claim => claim.ObservedAt)
            .ThenBy(claim => claim.InstallationId, StringComparer.Ordinal)
            .ToArray();

        if (ownedClaims.Length == 0)
        {
            return new(
                BringHereAvailability.Unavailable,
                Source: null,
                ConflictingClaims: [],
                "No snapshot location owned by the authenticated user is known for this World.");
        }

        var targetClaims = ownedClaims
            .Where(claim => string.Equals(
                claim.InstallationId,
                targetInstallationId,
                StringComparison.Ordinal))
            .ToArray();
        var remoteClaims = ownedClaims
            .Where(claim => !string.Equals(
                claim.InstallationId,
                targetInstallationId,
                StringComparison.Ordinal))
            .ToArray();

        if (remoteClaims.Length == 0)
        {
            return new(
                BringHereAvailability.AlreadyHere,
                targetClaims.FirstOrDefault(),
                ConflictingClaims: [],
                "The authenticated owner's only known snapshot location is already this installation.");
        }

        var distinctRemoteHeads = remoteClaims
            .GroupBy(claim => new
            {
                claim.StateRevisionId,
                claim.EnvironmentRevisionId
            })
            .Select(group => group.ToArray())
            .ToArray();
        if (distinctRemoteHeads.Length > 1)
        {
            return new(
                BringHereAvailability.Conflict,
                Source: null,
                ConflictingClaims: remoteClaims,
                "Owned installations report divergent state/environment heads. Safe World must not choose one automatically.");
        }

        var source = distinctRemoteHeads[0][0];
        var targetAtSameHead = targetClaims.Any(claim =>
            claim.StateRevisionId == source.StateRevisionId &&
            claim.EnvironmentRevisionId == source.EnvironmentRevisionId);
        if (targetAtSameHead)
        {
            return new(
                BringHereAvailability.AlreadyHere,
                source,
                ConflictingClaims: [],
                "This installation already has the same immutable state/environment head.");
        }

        if (targetClaims.Length > 0)
        {
            return new(
                BringHereAvailability.Conflict,
                Source: null,
                ConflictingClaims: targetClaims.Concat(remoteClaims).ToArray(),
                "This installation and another owned installation report different heads. Safe World must preserve both until ancestry is proven.");
        }

        return new(
            BringHereAvailability.Available,
            source,
            ConflictingClaims: [],
            "One unambiguous immutable state/environment head is available on another owned installation.");
    }

    private static bool SameOwner(
        OwnedWorldLocationClaim claim,
        UserIdentity owner)
        => string.Equals(claim.OwnerProvider, owner.Provider, StringComparison.Ordinal) &&
           string.Equals(claim.OwnerExternalId, owner.ExternalId, StringComparison.Ordinal);
}
