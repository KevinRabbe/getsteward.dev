using System.Text.Json;
using SharedWorlds.Core.Domain;
using SharedWorlds.Core.Worlds;

namespace SharedWorlds.Infrastructure.Storage;

internal sealed record PersistedStateRevision(
    StateRevision Revision,
    string? PayloadSha256);

internal static class StorageDocumentSchemas
{
    public static readonly PersistedDocumentSchema<World> World = new(
        "sharedworlds.world",
        CurrentVersion: 3,
        IntegrityRequiredFromVersion: 2,
        new Dictionary<int, Func<JsonElement, World>>
        {
            // Schema 0 is the pre-envelope format used by the initial foundation.
            [0] = payload => PersistedDocumentCodec.DeserializePayload<World>(
                payload,
                "sharedworlds.world"),
            // Schema 1 is the first versioned envelope and predates in-envelope integrity.
            [1] = payload => PersistedDocumentCodec.DeserializePayload<World>(
                payload,
                "sharedworlds.world"),
            // Schema 2 is integrity-protected but predates bounded named History checkpoints.
            // The World property's empty default keeps those Worlds compatible.
            [2] = payload => PersistedDocumentCodec.DeserializePayload<World>(
                payload,
                "sharedworlds.world")
        });

    public static readonly PersistedDocumentSchema<EnvironmentRevision> EnvironmentRevision =
        CreateProtected<EnvironmentRevision>("sharedworlds.environment-revision");

    public static readonly PersistedDocumentSchema<PersistedStateRevision> StateRevision = new(
        "sharedworlds.state-revision",
        CurrentVersion: 5,
        IntegrityRequiredFromVersion: 3,
        new Dictionary<int, Func<JsonElement, PersistedStateRevision>>
        {
            // Schema 0 is the pre-envelope format used by the initial foundation.
            [0] = payload => LegacyStateRevision(payload),
            // Schema 1 used the same logical StateRevision payload, before local payload
            // integrity became a required companion artifact for newly written revisions.
            [1] = payload => LegacyStateRevision(payload),
            // Schema 2 requires payload.sha256 for payload.bin but predates integrity protection
            // for revision.json itself.
            [2] = payload => LegacyStateRevision(payload),
            // Schema 3 protects revision.json itself but still stores payload integrity in the
            // separate payload.sha256 companion file.
            [3] = payload => LegacyStateRevision(payload),
            // Schema 4 binds payload integrity inside protected revision metadata but predates the
            // explicit state -> environment revision association. The nullable domain property keeps
            // those revisions readable as legacy History entries.
            [4] = payload => PersistedDocumentCodec.DeserializePayload<PersistedStateRevision>(
                payload,
                "sharedworlds.state-revision")
        });

    public static readonly PersistedDocumentSchema<WorkspaceRecoveryRecord> WorkspaceRecovery =
        CreateProtected<WorkspaceRecoveryRecord>("sharedworlds.workspace-recovery");

    public static readonly PersistedDocumentSchema<OwnedWorldLocationPublicationState>
        OwnedWorldLocationPublication =
            CreateProtected<OwnedWorldLocationPublicationState>(
                "sharedworlds.owned-world-location-publication");

    private static PersistedStateRevision LegacyStateRevision(JsonElement payload)
        => new(
            PersistedDocumentCodec.DeserializePayload<StateRevision>(
                payload,
                "sharedworlds.state-revision"),
            PayloadSha256: null);

    private static PersistedDocumentSchema<T> CreateProtected<T>(string documentType)
        => new(
            documentType,
            CurrentVersion: 2,
            IntegrityRequiredFromVersion: 2,
            new Dictionary<int, Func<JsonElement, T>>
            {
                // Schema 0 is the pre-envelope format used by the initial foundation.
                // Its root JSON object is the payload itself.
                [0] = payload => PersistedDocumentCodec.DeserializePayload<T>(payload, documentType),
                // Schema 1 is the first versioned envelope and predates in-envelope integrity.
                [1] = payload => PersistedDocumentCodec.DeserializePayload<T>(payload, documentType)
            });
}
