using System.Text.Json;
using SharedWorlds.Core.Domain;

namespace SharedWorlds.Infrastructure.Storage;

internal static class StorageDocumentSchemas
{
    public static readonly PersistedDocumentSchema<World> World = CreateProtected<World>(
        "sharedworlds.world");

    public static readonly PersistedDocumentSchema<EnvironmentRevision> EnvironmentRevision =
        CreateProtected<EnvironmentRevision>("sharedworlds.environment-revision");

    public static readonly PersistedDocumentSchema<StateRevision> StateRevision = new(
        "sharedworlds.state-revision",
        CurrentVersion: 3,
        IntegrityRequiredFromVersion: 3,
        new Dictionary<int, Func<JsonElement, StateRevision>>
        {
            // Schema 0 is the pre-envelope format used by the initial foundation.
            [0] = payload => PersistedDocumentCodec.DeserializePayload<StateRevision>(
                payload,
                "sharedworlds.state-revision"),
            // Schema 1 used the same logical StateRevision payload, before local payload
            // integrity became a required companion artifact for newly written revisions.
            [1] = payload => PersistedDocumentCodec.DeserializePayload<StateRevision>(
                payload,
                "sharedworlds.state-revision"),
            // Schema 2 requires payload.sha256 for payload.bin but predates integrity protection
            // for revision.json itself.
            [2] = payload => PersistedDocumentCodec.DeserializePayload<StateRevision>(
                payload,
                "sharedworlds.state-revision")
        });

    public static readonly PersistedDocumentSchema<WorkspaceRecoveryRecord> WorkspaceRecovery =
        CreateProtected<WorkspaceRecoveryRecord>("sharedworlds.workspace-recovery");

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
