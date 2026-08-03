using SharedWorlds.Core.Domain;
using SharedWorlds.Core.Environment;

namespace SharedWorlds.Core.Worlds;

public enum OwnedWorldSnapshotWriteResult
{
    Created,
    NoChange,
    Conflict
}

/// <summary>
/// Immutable owner-private transfer evidence for one exact location head. The opaque object key is
/// backend-only storage identity; clients receive scoped download authorization rather than this key.
/// </summary>
public sealed record OwnedWorldSnapshot(
    WorldId WorldId,
    string OwnerProvider,
    string OwnerExternalId,
    string InstallationId,
    RevisionId StateRevisionId,
    RevisionId EnvironmentRevisionId,
    string GameAdapterId,
    string StatePackageObjectKey,
    long StatePackageByteSize,
    string StatePackageSha256,
    EnvironmentManifest EnvironmentManifest,
    DateTimeOffset PublishedAt)
{
    public const long MaximumStatePackageByteSize = 20L * 1024 * 1024 * 1024;
    public const int MaximumInstallationIdLength = 128;
    public const int MaximumGameAdapterIdLength = 128;
    public const int MaximumObjectKeyLength = 512;
    public const int MaximumGameVersionLength = 256;
    public const int MaximumEnvironmentComponents = 4_096;
    public const int MaximumEnvironmentConfigurationEntries = 4_096;
    public const int MaximumEnvironmentMetadataEntries = 1_024;
    public const int MaximumEnvironmentTextLength = 1_024;

    public void Validate()
    {
        if (WorldId.Value == Guid.Empty)
        {
            throw new InvalidDataException("Private snapshot World ID is required.");
        }

        if (StateRevisionId.Value == Guid.Empty || EnvironmentRevisionId.Value == Guid.Empty)
        {
            throw new InvalidDataException(
                "Private snapshot state and environment revision IDs are required.");
        }

        ValidateText(OwnerProvider, "Owner provider", 128);
        ValidateText(OwnerExternalId, "Owner external ID", 256);
        ValidateText(
            InstallationId,
            "Installation ID",
            MaximumInstallationIdLength);
        ValidateText(
            GameAdapterId,
            "Game adapter ID",
            MaximumGameAdapterIdLength);
        ValidateText(
            StatePackageObjectKey,
            "State package object key",
            MaximumObjectKeyLength);

        if (StatePackageByteSize is < 1 or > MaximumStatePackageByteSize)
        {
            throw new InvalidDataException(
                $"Private snapshot state package size must be between 1 and {MaximumStatePackageByteSize} bytes.");
        }

        ValidateSha256(StatePackageSha256);
        if (PublishedAt == default)
        {
            throw new InvalidDataException("Private snapshot publication time is required.");
        }

        ValidateManifest(EnvironmentManifest, GameAdapterId);
    }

    private static void ValidateManifest(
        EnvironmentManifest? manifest,
        string expectedAdapterId)
    {
        if (manifest is null)
        {
            throw new InvalidDataException("Private snapshot environment manifest is required.");
        }

        if (manifest.SchemaVersion <= 0)
        {
            throw new InvalidDataException(
                "Private snapshot environment manifest schema version must be positive.");
        }

        ValidateText(
            manifest.AdapterId,
            "Environment manifest adapter ID",
            MaximumGameAdapterIdLength);
        if (!string.Equals(manifest.AdapterId, expectedAdapterId, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "Private snapshot environment manifest belongs to a different game adapter.");
        }

        ValidateText(
            manifest.GameVersion,
            "Environment manifest game version",
            MaximumGameVersionLength);
        if (manifest.Components is null ||
            manifest.Components.Count > MaximumEnvironmentComponents)
        {
            throw new InvalidDataException(
                $"Private snapshot environment manifest must contain at most {MaximumEnvironmentComponents} components.");
        }

        if (manifest.Configuration is null ||
            manifest.Configuration.Count > MaximumEnvironmentConfigurationEntries)
        {
            throw new InvalidDataException(
                $"Private snapshot environment manifest must contain at most {MaximumEnvironmentConfigurationEntries} configuration entries.");
        }

        foreach (var component in manifest.Components)
        {
            if (component is null)
            {
                throw new InvalidDataException(
                    "Private snapshot environment manifest contains a null component.");
            }

            ValidateText(
                component.Kind,
                "Environment component kind",
                MaximumEnvironmentTextLength);
            ValidateText(
                component.Id,
                "Environment component ID",
                MaximumEnvironmentTextLength);
            ValidateOptionalText(
                component.Version,
                "Environment component version",
                MaximumEnvironmentTextLength);
            ValidateOptionalText(
                component.Source,
                "Environment component source",
                MaximumEnvironmentTextLength);
            if (component.Metadata is { } metadata)
            {
                if (metadata.Count > MaximumEnvironmentMetadataEntries)
                {
                    throw new InvalidDataException(
                        $"Environment component metadata must contain at most {MaximumEnvironmentMetadataEntries} entries.");
                }

                ValidateDictionary(metadata, "Environment component metadata");
            }
        }

        ValidateDictionary(manifest.Configuration, "Environment configuration");
    }

    private static void ValidateDictionary(
        IReadOnlyDictionary<string, string> values,
        string name)
    {
        foreach (var pair in values)
        {
            ValidateText(
                pair.Key,
                $"{name} key",
                MaximumEnvironmentTextLength);
            ValidateText(
                pair.Value,
                $"{name} value",
                MaximumEnvironmentTextLength);
        }
    }

    private static void ValidateSha256(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length != 64)
        {
            throw new InvalidDataException(
                "Private snapshot state package SHA-256 must contain exactly 64 hexadecimal characters.");
        }

        try
        {
            if (Convert.FromHexString(value).Length != 32)
            {
                throw new InvalidDataException(
                    "Private snapshot state package SHA-256 has an invalid length.");
            }
        }
        catch (FormatException exception)
        {
            throw new InvalidDataException(
                "Private snapshot state package SHA-256 is not valid hexadecimal.",
                exception);
        }
    }

    private static void ValidateOptionalText(
        string? value,
        string name,
        int maximumLength)
    {
        if (value is null)
        {
            return;
        }

        ValidateText(value, name, maximumLength);
    }

    private static void ValidateText(
        string value,
        string name,
        int maximumLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidDataException($"{name} is required.");
        }

        if (value.Length > maximumLength)
        {
            throw new InvalidDataException(
                $"{name} must not exceed {maximumLength} characters.");
        }

        if (value.Any(char.IsControl))
        {
            throw new InvalidDataException($"{name} cannot contain control characters.");
        }
    }
}

public sealed record OwnedWorldSnapshotWriteDecision(
    OwnedWorldSnapshotWriteResult Result,
    OwnedWorldSnapshot Current,
    string Reason);

/// <summary>
/// Atomic persistence boundary for verified owner-private snapshot descriptors. Implementations must
/// treat the exact owner/World/installation/state/environment key as immutable: identical publication
/// is idempotent, while different metadata for the same key is a conflict.
/// </summary>
public interface IOwnedWorldSnapshotStore
{
    Task<OwnedWorldSnapshot?> LoadExactAsync(
        string ownerProvider,
        string ownerExternalId,
        WorldId worldId,
        string installationId,
        RevisionId stateRevisionId,
        RevisionId environmentRevisionId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<OwnedWorldSnapshot>> ListWorldSnapshotsAsync(
        string ownerProvider,
        string ownerExternalId,
        WorldId worldId,
        int maximumSnapshots,
        CancellationToken cancellationToken = default);

    Task<OwnedWorldSnapshotWriteDecision> PublishAsync(
        OwnedWorldSnapshot snapshot,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Publishes immutable private snapshot evidence only for the authenticated owner's registered source
/// installation and its exact currently advertised location head.
/// </summary>
public sealed class OwnedWorldSnapshotRegistry
{
    private readonly IOwnedWorldLocationStore _locations;
    private readonly IOwnedWorldSnapshotStore _snapshots;

    public OwnedWorldSnapshotRegistry(
        IOwnedWorldLocationStore locations,
        IOwnedWorldSnapshotStore snapshots)
    {
        ArgumentNullException.ThrowIfNull(locations);
        ArgumentNullException.ThrowIfNull(snapshots);
        _locations = locations;
        _snapshots = snapshots;
    }

    public async Task<OwnedWorldSnapshotWriteDecision> PublishAsync(
        UserIdentity authenticatedOwner,
        string installationId,
        WorldId worldId,
        RevisionId stateRevisionId,
        RevisionId environmentRevisionId,
        string gameAdapterId,
        string statePackageObjectKey,
        long statePackageByteSize,
        string statePackageSha256,
        EnvironmentManifest environmentManifest,
        DateTimeOffset publishedAt,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(authenticatedOwner);

        var installation = await _locations.GetInstallationAsync(
            installationId,
            cancellationToken)
            ?? throw new InvalidOperationException(
                "The source installation must be registered before it can publish a private World snapshot.");
        if (!SameOwner(installation, authenticatedOwner))
        {
            throw new InvalidOperationException(
                "The authenticated owner does not own the source installation.");
        }

        var locationClaims = await _locations.ListWorldLocationsAsync(
            worldId,
            authenticatedOwner.Provider,
            authenticatedOwner.ExternalId,
            cancellationToken);
        var sourceClaims = locationClaims
            .Where(claim => string.Equals(
                claim.InstallationId,
                installationId,
                StringComparison.Ordinal))
            .ToArray();
        if (sourceClaims.Length != 1)
        {
            throw new InvalidOperationException(
                sourceClaims.Length == 0
                    ? "The source installation must publish its exact private World location before snapshot evidence can be finalized."
                    : "The source installation has ambiguous duplicate location claims.");
        }

        var source = sourceClaims[0];
        if (source.StateRevisionId != stateRevisionId ||
            source.EnvironmentRevisionId != environmentRevisionId)
        {
            throw new InvalidOperationException(
                "Private snapshot evidence must match the source installation's exact currently advertised state/environment head.");
        }

        if (source.Presentation is { } presentation &&
            !string.Equals(
                presentation.GameAdapterId,
                gameAdapterId,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Private snapshot adapter identity disagrees with the source location presentation.");
        }

        var snapshot = new OwnedWorldSnapshot(
            worldId,
            authenticatedOwner.Provider,
            authenticatedOwner.ExternalId,
            installationId,
            stateRevisionId,
            environmentRevisionId,
            gameAdapterId,
            statePackageObjectKey,
            statePackageByteSize,
            statePackageSha256.ToUpperInvariant(),
            environmentManifest,
            publishedAt);
        snapshot.Validate();
        return await _snapshots.PublishAsync(snapshot, cancellationToken);
    }

    private static bool SameOwner(
        OwnedInstallationRegistration installation,
        UserIdentity owner)
        => string.Equals(
               installation.OwnerProvider,
               owner.Provider,
               StringComparison.Ordinal) &&
           string.Equals(
               installation.OwnerExternalId,
               owner.ExternalId,
               StringComparison.Ordinal);
}

public sealed record BringHereSnapshotDecision(
    BringHereAvailability Availability,
    OwnedWorldLocationClaim? Source,
    OwnedWorldSnapshot? Snapshot,
    IReadOnlyList<OwnedWorldLocationClaim> ConflictingClaims,
    string Reason);

/// <summary>
/// Adds immutable-byte readiness to Bring Here head authority. A location decision can be Available
/// only when exactly one verified snapshot descriptor matches its selected source installation and
/// exact state/environment head. Timestamps never substitute for missing or conflicting evidence.
/// </summary>
public sealed class BringHereSnapshotAuthorityService
{
    public const int MaximumSnapshotsPerWorld = 10_000;

    private readonly BringHereAuthorityService _headAuthority = new();

    public BringHereSnapshotDecision Resolve(
        WorldId worldId,
        UserIdentity authenticatedOwner,
        string targetInstallationId,
        IEnumerable<OwnedWorldLocationClaim> claims,
        IEnumerable<OwnedWorldSnapshot> snapshots)
    {
        ArgumentNullException.ThrowIfNull(snapshots);
        var headDecision = _headAuthority.Resolve(
            worldId,
            authenticatedOwner,
            targetInstallationId,
            claims);
        if (headDecision.Availability != BringHereAvailability.Available)
        {
            return new BringHereSnapshotDecision(
                headDecision.Availability,
                headDecision.Source,
                Snapshot: null,
                headDecision.ConflictingClaims,
                headDecision.Reason);
        }

        var source = headDecision.Source
            ?? throw new InvalidDataException(
                "Bring Here head authority returned Available without a source claim.");
        var ownedSnapshots = snapshots
            .Where(snapshot => SameOwner(snapshot, authenticatedOwner))
            .Where(snapshot => snapshot.WorldId == worldId)
            .Take(MaximumSnapshotsPerWorld + 1)
            .ToArray();
        if (ownedSnapshots.Length > MaximumSnapshotsPerWorld)
        {
            throw new InvalidOperationException(
                $"Private World '{worldId}' exceeded the bounded limit of {MaximumSnapshotsPerWorld} snapshot descriptors.");
        }

        var exact = ownedSnapshots
            .Where(snapshot => string.Equals(
                snapshot.InstallationId,
                source.InstallationId,
                StringComparison.Ordinal))
            .Where(snapshot => snapshot.StateRevisionId == source.StateRevisionId)
            .Where(snapshot => snapshot.EnvironmentRevisionId == source.EnvironmentRevisionId)
            .ToArray();
        if (exact.Length == 0)
        {
            return new BringHereSnapshotDecision(
                BringHereAvailability.Unavailable,
                source,
                Snapshot: null,
                ConflictingClaims: [],
                "The exact remote head is known, but its verified private snapshot bytes and environment metadata are not available yet.");
        }

        if (exact.Length > 1)
        {
            throw new InvalidDataException(
                "The exact private World source head has duplicate snapshot descriptors.");
        }

        var selected = exact[0];
        selected.Validate();
        if (source.Presentation is { } presentation &&
            !string.Equals(
                selected.GameAdapterId,
                presentation.GameAdapterId,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "Private snapshot adapter identity disagrees with its selected location claim.");
        }

        return new BringHereSnapshotDecision(
            BringHereAvailability.Available,
            source,
            selected,
            ConflictingClaims: [],
            "One unambiguous remote head has exact verified state-package and environment-manifest evidence.");
    }

    private static bool SameOwner(
        OwnedWorldSnapshot snapshot,
        UserIdentity owner)
        => string.Equals(
               snapshot.OwnerProvider,
               owner.Provider,
               StringComparison.Ordinal) &&
           string.Equals(
               snapshot.OwnerExternalId,
               owner.ExternalId,
               StringComparison.Ordinal);
}
