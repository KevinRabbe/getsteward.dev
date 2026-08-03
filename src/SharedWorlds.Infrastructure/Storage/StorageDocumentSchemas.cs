using System.Text.Json;
using SharedWorlds.Core.Domain;
using SharedWorlds.Core.Worlds;

namespace SharedWorlds.Infrastructure.Storage;

internal sealed record PersistedStateRevision(
    StateRevision Revision,
    string? PayloadSha256);

internal sealed record OwnedWorldLocationPublicationOperationV3(
    OwnedWorldLocationPublicationOperationKind Kind,
    RevisionId? StateRevisionId,
    RevisionId? EnvironmentRevisionId,
    RevisionId? ExpectedStateRevisionId,
    RevisionId? ExpectedEnvironmentRevisionId);

internal sealed record OwnedWorldLocationPublicationStateV3(
    WorldId WorldId,
    string InstallationId,
    RevisionId? DesiredStateRevisionId,
    RevisionId? DesiredEnvironmentRevisionId,
    RevisionId? ConfirmedStateRevisionId,
    RevisionId? ConfirmedEnvironmentRevisionId,
    OwnedWorldLocationPublicationOperationV3? InFlight,
    DateTimeOffset UpdatedAt);

internal static class StorageDocumentSchemas
{
    public static readonly PersistedDocumentSchema<World> World = new(
        "sharedworlds.world",
        CurrentVersion: 3,
        IntegrityRequiredFromVersion: 2,
        new Dictionary<int, Func<JsonElement, World>>
        {
            [0] = payload => PersistedDocumentCodec.DeserializePayload<World>(payload, "sharedworlds.world"),
            [1] = payload => PersistedDocumentCodec.DeserializePayload<World>(payload, "sharedworlds.world"),
            [2] = payload => PersistedDocumentCodec.DeserializePayload<World>(payload, "sharedworlds.world")
        });

    public static readonly PersistedDocumentSchema<EnvironmentRevision> EnvironmentRevision =
        CreateProtected<EnvironmentRevision>("sharedworlds.environment-revision");

    public static readonly PersistedDocumentSchema<PersistedStateRevision> StateRevision = new(
        "sharedworlds.state-revision",
        CurrentVersion: 5,
        IntegrityRequiredFromVersion: 3,
        new Dictionary<int, Func<JsonElement, PersistedStateRevision>>
        {
            [0] = payload => LegacyStateRevision(payload),
            [1] = payload => LegacyStateRevision(payload),
            [2] = payload => LegacyStateRevision(payload),
            [3] = payload => LegacyStateRevision(payload),
            [4] = payload => PersistedDocumentCodec.DeserializePayload<PersistedStateRevision>(
                payload,
                "sharedworlds.state-revision")
        });

    public static readonly PersistedDocumentSchema<WorkspaceRecoveryRecord> WorkspaceRecovery =
        CreateProtected<WorkspaceRecoveryRecord>("sharedworlds.workspace-recovery");

    public static readonly PersistedDocumentSchema<OwnedWorldLocationPublicationState>
        OwnedWorldLocationPublication = new(
            "sharedworlds.owned-world-location-publication",
            CurrentVersion: 4,
            IntegrityRequiredFromVersion: 2,
            new Dictionary<int, Func<JsonElement, OwnedWorldLocationPublicationState>>
            {
                [2] = RejectUnboundOwnedWorldLocationPublication,
                [3] = UpgradeInstallationBoundPublicationV3
            });

    private static PersistedStateRevision LegacyStateRevision(JsonElement payload)
        => new(
            PersistedDocumentCodec.DeserializePayload<StateRevision>(
                payload,
                "sharedworlds.state-revision"),
            PayloadSha256: null);

    private static OwnedWorldLocationPublicationState RejectUnboundOwnedWorldLocationPublication(
        JsonElement _)
        => throw new InvalidDataException(
            "This owned-World location journal predates exact installation-ID binding and cannot be replayed safely.");

    private static OwnedWorldLocationPublicationState UpgradeInstallationBoundPublicationV3(
        JsonElement payload)
    {
        var legacy = PersistedDocumentCodec.DeserializePayload<OwnedWorldLocationPublicationStateV3>(
            payload,
            "sharedworlds.owned-world-location-publication");
        var inFlight = legacy.InFlight is null
            ? null
            : new OwnedWorldLocationPublicationOperation(
                legacy.InFlight.Kind,
                legacy.InFlight.StateRevisionId,
                legacy.InFlight.EnvironmentRevisionId,
                legacy.InFlight.ExpectedStateRevisionId,
                legacy.InFlight.ExpectedEnvironmentRevisionId,
                Presentation: null);
        return new OwnedWorldLocationPublicationState(
            legacy.WorldId,
            legacy.InstallationId,
            legacy.DesiredStateRevisionId,
            legacy.DesiredEnvironmentRevisionId,
            legacy.ConfirmedStateRevisionId,
            legacy.ConfirmedEnvironmentRevisionId,
            inFlight,
            legacy.UpdatedAt,
            DesiredPresentation: null,
            ConfirmedPresentation: null);
    }

    private static PersistedDocumentSchema<T> CreateProtected<T>(string documentType)
        => new(
            documentType,
            CurrentVersion: 2,
            IntegrityRequiredFromVersion: 2,
            new Dictionary<int, Func<JsonElement, T>>
            {
                [0] = payload => PersistedDocumentCodec.DeserializePayload<T>(payload, documentType),
                [1] = payload => PersistedDocumentCodec.DeserializePayload<T>(payload, documentType)
            });
}
