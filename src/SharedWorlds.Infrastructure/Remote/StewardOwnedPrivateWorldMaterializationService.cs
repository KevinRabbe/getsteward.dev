using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using SharedWorlds.Core.Abstractions;
using SharedWorlds.Core.Domain;
using SharedWorlds.Core.Worlds;

namespace SharedWorlds.Infrastructure.Remote;

public enum OwnedPrivateWorldMaterializationStatus
{
    Materialized,
    AlreadyMaterialized
}

public sealed record OwnedPrivateWorldMaterializationResult(
    OwnedPrivateWorldMaterializationStatus Status,
    World World);

/// <summary>
/// Commits one fully prepared owner-private World into canonical local storage while preserving the
/// exact remote World and revision identities. Catalog presentation and selected-head evidence must
/// agree before any write. Immutable revisions are compared before reuse, package bytes are verified
/// before the first mutation, and discoverable World metadata is always published last.
/// </summary>
public sealed class StewardOwnedPrivateWorldMaterializationService
{
    private const int WorldGateCount = 64;
    private static readonly JsonSerializerOptions ComparisonJsonOptions =
        new(JsonSerializerDefaults.Web);

    private readonly IWorldStorage _storage;
    private readonly VerifiedPackageCache _cache;
    private readonly SemaphoreSlim[] _worldGates = Enumerable
        .Range(0, WorldGateCount)
        .Select(static _ => new SemaphoreSlim(1, 1))
        .ToArray();

    public StewardOwnedPrivateWorldMaterializationService(
        IWorldStorage storage,
        VerifiedPackageCache cache)
    {
        ArgumentNullException.ThrowIfNull(storage);
        ArgumentNullException.ThrowIfNull(cache);
        _storage = storage;
        _cache = cache;
    }

    public async Task<OwnedPrivateWorldMaterializationResult> MaterializeAsync(
        StewardOwnedPrivateWorldCatalogEntry catalogEntry,
        RemoteVerifiedPrivateSnapshotMaterialization prepared,
        UserIdentity localOwner,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(catalogEntry);
        ArgumentNullException.ThrowIfNull(prepared);
        ArgumentNullException.ThrowIfNull(localOwner);
        cancellationToken.ThrowIfCancellationRequested();

        var desiredWorld = ValidateAndCreateWorld(
            catalogEntry,
            prepared,
            localOwner);
        var gate = _worldGates[GetGateIndex(desiredWorld.Id)];
        await gate.WaitAsync(cancellationToken);
        try
        {
            return await MaterializeLockedAsync(
                desiredWorld,
                prepared,
                cancellationToken);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<OwnedPrivateWorldMaterializationResult> MaterializeLockedAsync(
        World desiredWorld,
        RemoteVerifiedPrivateSnapshotMaterialization prepared,
        CancellationToken cancellationToken)
    {
        var plan = prepared.Plan;
        var currentWorld = await _storage.LoadWorldAsync(
            desiredWorld.Id,
            cancellationToken);
        if (currentWorld is not null)
        {
            EnsureEquivalent(currentWorld, desiredWorld, "local World metadata");
            var currentEnvironment = await _storage.LoadEnvironmentRevisionAsync(
                desiredWorld.Id,
                plan.EnvironmentRevisionId,
                cancellationToken)
                ?? throw new InvalidDataException(
                    "The existing local World references missing exact environment metadata.");
            EnsureEquivalent(
                currentEnvironment,
                plan.EnvironmentRevision,
                "existing environment revision");

            var currentState = await _storage.LoadStateRevisionAsync(
                desiredWorld.Id,
                plan.StateRevisionId,
                cancellationToken)
                ?? throw new InvalidDataException(
                    "The existing local World references missing exact state metadata.");
            EnsureEquivalent(
                currentState,
                plan.StateRevision,
                "existing state revision");
            await VerifyExistingPayloadAsync(plan, cancellationToken);
            return new(
                OwnedPrivateWorldMaterializationStatus.AlreadyMaterialized,
                currentWorld);
        }

        var existingEnvironment = await _storage.LoadEnvironmentRevisionAsync(
            desiredWorld.Id,
            plan.EnvironmentRevisionId,
            cancellationToken);
        if (existingEnvironment is not null)
        {
            EnsureEquivalent(
                existingEnvironment,
                plan.EnvironmentRevision,
                "partially materialized environment revision");
        }

        var existingState = await _storage.LoadStateRevisionAsync(
            desiredWorld.Id,
            plan.StateRevisionId,
            cancellationToken);
        if (existingState is not null)
        {
            EnsureEquivalent(
                existingState,
                plan.StateRevision,
                "partially materialized state revision");
            await VerifyExistingPayloadAsync(plan, cancellationToken);
        }

        await using var verifiedPackage = existingState is null
            ? await _cache.OpenVerifiedReadAsync(
                plan.Authorization,
                cancellationToken)
            : null;

        if (existingEnvironment is null)
        {
            await _storage.StoreEnvironmentRevisionAsync(
                plan.EnvironmentRevision,
                cancellationToken);
        }

        if (existingState is null)
        {
            if (verifiedPackage is null)
            {
                throw new InvalidDataException(
                    "Exact state bytes were not opened before local materialization.");
            }

            verifiedPackage.Position = 0;
            await _storage.StoreRevisionAsync(
                plan.StateRevision,
                verifiedPackage,
                cancellationToken);
        }

        await _storage.SaveWorldAsync(desiredWorld, cancellationToken);
        return new(
            OwnedPrivateWorldMaterializationStatus.Materialized,
            desiredWorld);
    }

    private async Task VerifyExistingPayloadAsync(
        RemotePrivateSnapshotMaterializationPlan plan,
        CancellationToken cancellationToken)
    {
        if (!await _storage.IsRevisionPayloadAvailableAsync(
                plan.WorldId,
                plan.StateRevisionId,
                cancellationToken))
        {
            throw new InvalidDataException(
                "The exact local state revision exists without its immutable payload.");
        }

        var byteSize = await _storage.GetRevisionPayloadSizeAsync(
            plan.WorldId,
            plan.StateRevisionId,
            cancellationToken);
        if (byteSize != plan.ExpectedByteSize)
        {
            throw new InvalidDataException(
                "The exact local state payload byte size disagrees with remote materialization evidence.");
        }

        await using var payload = await _storage.OpenRevisionAsync(
            plan.WorldId,
            plan.StateRevisionId,
            cancellationToken);
        var sha256 = Convert.ToHexString(
            await SHA256.HashDataAsync(payload, cancellationToken));
        if (!string.Equals(
                sha256,
                plan.ExpectedSha256,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "The exact local state payload SHA-256 disagrees with remote materialization evidence.");
        }
    }

    private static World ValidateAndCreateWorld(
        StewardOwnedPrivateWorldCatalogEntry catalogEntry,
        RemoteVerifiedPrivateSnapshotMaterialization prepared,
        UserIdentity localOwner)
    {
        var plan = prepared.Plan;
        var source = catalogEntry.Source;
        if (catalogEntry.Availability != BringHereAvailability.Available ||
            source is null ||
            catalogEntry.ConflictingClaims.Count != 0 ||
            string.IsNullOrWhiteSpace(catalogEntry.GameAdapterId))
        {
            throw new InvalidOperationException(
                "Only one unambiguous Available private World catalog entry can be materialized.");
        }

        if (catalogEntry.WorldId != plan.WorldId ||
            source.WorldId != plan.WorldId ||
            !string.Equals(
                source.InstallationId,
                plan.SourceInstallationId,
                StringComparison.Ordinal) ||
            source.StateRevisionId != plan.StateRevisionId ||
            source.EnvironmentRevisionId != plan.EnvironmentRevisionId ||
            !string.Equals(
                catalogEntry.GameAdapterId,
                plan.GameAdapterId,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "Private World catalog selection disagrees with the prepared exact materialization plan.");
        }

        if (source.Presentation is { } presentation &&
            (!string.Equals(
                 presentation.Name,
                 catalogEntry.Name,
                 StringComparison.Ordinal) ||
             !string.Equals(
                 presentation.GameAdapterId,
                 plan.GameAdapterId,
                 StringComparison.Ordinal)))
        {
            throw new InvalidDataException(
                "Private World source presentation disagrees with the authenticated catalog entry.");
        }

        if (prepared.Package.ByteSize != plan.ExpectedByteSize ||
            !string.Equals(
                prepared.Package.Sha256,
                plan.ExpectedSha256,
                StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(prepared.Package.Path) ||
            plan.Authorization.ExpectedByteSize != plan.ExpectedByteSize ||
            !string.Equals(
                plan.Authorization.ExpectedSha256,
                plan.ExpectedSha256,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "Prepared cache evidence disagrees with the exact materialization package identity.");
        }

        var descriptor = new OwnedWorldSnapshot(
            plan.WorldId,
            "client-validation",
            "client-validation",
            plan.SourceInstallationId,
            plan.StateRevisionId,
            plan.EnvironmentRevisionId,
            plan.GameAdapterId,
            "client-validation",
            plan.ExpectedByteSize,
            plan.ExpectedSha256,
            plan.EnvironmentManifest,
            DateTimeOffset.UtcNow);
        var revisionEvidence = new OwnedWorldSnapshotRevisionEvidence(
            "client-validation",
            "client-validation",
            plan.SourceInstallationId,
            plan.WorldId,
            plan.StateRevision,
            plan.EnvironmentRevision,
            DateTimeOffset.UtcNow);
        revisionEvidence.ValidateAgainst(descriptor);

        return new World(
            plan.WorldId,
            catalogEntry.Name,
            plan.GameAdapterId,
            Members: [localOwner],
            CurrentEnvironmentRevisionId: plan.EnvironmentRevisionId,
            CurrentStateRevisionId: plan.StateRevisionId);
    }

    private static void EnsureEquivalent<T>(
        T current,
        T expected,
        string description)
    {
        var currentNode = JsonSerializer.SerializeToNode(
            current,
            ComparisonJsonOptions);
        var expectedNode = JsonSerializer.SerializeToNode(
            expected,
            ComparisonJsonOptions);
        if (!JsonNode.DeepEquals(currentNode, expectedNode))
        {
            throw new InvalidDataException(
                $"The {description} conflicts with the exact remote identity and will not be overwritten.");
        }
    }

    private static int GetGateIndex(WorldId worldId)
        => (worldId.Value.GetHashCode() & int.MaxValue) % WorldGateCount;
}
