using SharedWorlds.Core.Domain;

namespace SharedWorlds.Core.Worlds;

public interface IOwnedWorldLocationCatalogStore
{
    Task<IReadOnlyList<OwnedWorldLocationClaim>> ListOwnerWorldLocationsAsync(
        string ownerProvider,
        string ownerExternalId,
        int maximumClaims,
        CancellationToken cancellationToken = default);
}

public sealed record OwnedPrivateWorldCatalogEntry(
    WorldId WorldId,
    string Name,
    string? GameAdapterId,
    BringHereAvailability Availability,
    OwnedWorldLocationClaim? Source,
    IReadOnlyList<OwnedWorldLocationClaim> ConflictingClaims,
    string Reason);

/// <summary>
/// Builds a bounded owner-private discovery catalog without weakening Bring Here authority. A World
/// is discoverable only when at least one owned location carries complete presentation metadata. Every
/// location claim, including presentationless legacy claims, still participates in head resolution.
/// </summary>
public sealed class OwnedPrivateWorldCatalogService
{
    public const int MaximumClaims = 10_000;
    public const int MaximumWorlds = 1_000;

    private readonly BringHereAuthorityService _authority = new();

    public IReadOnlyList<OwnedPrivateWorldCatalogEntry> Resolve(
        UserIdentity authenticatedOwner,
        string targetInstallationId,
        IEnumerable<OwnedWorldLocationClaim> claims)
    {
        ArgumentNullException.ThrowIfNull(authenticatedOwner);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetInstallationId);
        ArgumentNullException.ThrowIfNull(claims);

        var ownedClaims = claims
            .Where(claim => SameOwner(claim, authenticatedOwner))
            .Take(MaximumClaims + 1)
            .ToArray();
        if (ownedClaims.Length > MaximumClaims)
        {
            throw new InvalidOperationException(
                $"The private World catalog exceeded the bounded limit of {MaximumClaims} location claims.");
        }

        var groups = ownedClaims
            .GroupBy(claim => claim.WorldId)
            .ToArray();
        if (groups.Length > MaximumWorlds)
        {
            throw new InvalidOperationException(
                $"The private World catalog exceeded the bounded limit of {MaximumWorlds} Worlds.");
        }

        var entries = new List<OwnedPrivateWorldCatalogEntry>(groups.Length);
        foreach (var group in groups)
        {
            var worldClaims = group
                .OrderByDescending(claim => claim.ObservedAt)
                .ThenBy(claim => claim.InstallationId, StringComparer.Ordinal)
                .ToArray();
            EnsureDistinctInstallations(group.Key, worldClaims);

            var presentedClaims = worldClaims
                .Where(claim => claim.Presentation is not null)
                .ToArray();
            if (presentedClaims.Length == 0)
            {
                continue;
            }

            foreach (var claim in presentedClaims)
            {
                claim.Presentation!.Validate();
            }

            var newestPresentation = presentedClaims[0].Presentation!;
            var adapterIds = presentedClaims
                .Select(claim => claim.Presentation!.GameAdapterId)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            if (adapterIds.Length > 1)
            {
                entries.Add(new OwnedPrivateWorldCatalogEntry(
                    group.Key,
                    newestPresentation.Name,
                    GameAdapterId: null,
                    BringHereAvailability.Conflict,
                    Source: null,
                    ConflictingClaims: presentedClaims,
                    "Owned installations disagree on the immutable game adapter ID. Safe World cannot choose one automatically."));
                continue;
            }

            var decision = _authority.Resolve(
                group.Key,
                authenticatedOwner,
                targetInstallationId,
                worldClaims);
            entries.Add(new OwnedPrivateWorldCatalogEntry(
                group.Key,
                newestPresentation.Name,
                adapterIds[0],
                decision.Availability,
                decision.Source,
                decision.ConflictingClaims,
                decision.Reason));
        }

        return entries
            .OrderBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(entry => entry.WorldId.ToString(), StringComparer.Ordinal)
            .ToArray();
    }

    private static void EnsureDistinctInstallations(
        WorldId worldId,
        IReadOnlyList<OwnedWorldLocationClaim> claims)
    {
        var duplicate = claims
            .GroupBy(claim => claim.InstallationId, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
        {
            throw new InvalidDataException(
                $"Private World '{worldId}' contains duplicate claims for installation '{duplicate.Key}'.");
        }
    }

    private static bool SameOwner(
        OwnedWorldLocationClaim claim,
        UserIdentity owner)
        => string.Equals(claim.OwnerProvider, owner.Provider, StringComparison.Ordinal) &&
           string.Equals(claim.OwnerExternalId, owner.ExternalId, StringComparison.Ordinal);
}
