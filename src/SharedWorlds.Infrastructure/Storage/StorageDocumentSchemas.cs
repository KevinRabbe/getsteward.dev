using System.Text.Json;
using SharedWorlds.Core.Domain;

namespace SharedWorlds.Infrastructure.Storage;

internal static class StorageDocumentSchemas
{
    public static readonly PersistedDocumentSchema<World> World = Create<World>(
        "sharedworlds.world");

    public static readonly PersistedDocumentSchema<EnvironmentRevision> EnvironmentRevision =
        Create<EnvironmentRevision>("sharedworlds.environment-revision");

    public static readonly PersistedDocumentSchema<StateRevision> StateRevision = new(
        "sharedworlds.state-revision",
        CurrentVersion: 2,
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
                "sharedworlds.state-revision")
        });

    public static readonly PersistedDocumentSchema<WorkspaceRecoveryRecord> WorkspaceRecovery =
        Create<WorkspaceRecoveryRecord>("sharedworlds.workspace-recovery");

    private static PersistedDocumentSchema<T> Create<T>(string documentType)
        => new(
            documentType,
            CurrentVersion: 1,
            new Dictionary<int, Func<JsonElement, T>>
            {
                // Schema 0 is the pre-envelope format used by the initial foundation.
                // Its root JSON object is the payload itself.
                [0] = payload => PersistedDocumentCodec.DeserializePayload<T>(payload, documentType)
            });
}
