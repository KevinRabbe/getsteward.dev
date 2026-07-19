using System.Text.Json;
using SharedWorlds.Core.Domain;

namespace SharedWorlds.Infrastructure.Storage;

internal static class StorageDocumentSchemas
{
    public static readonly PersistedDocumentSchema<World> World = Create<World>(
        "sharedworlds.world");

    public static readonly PersistedDocumentSchema<EnvironmentRevision> EnvironmentRevision =
        Create<EnvironmentRevision>("sharedworlds.environment-revision");

    public static readonly PersistedDocumentSchema<StateRevision> StateRevision =
        Create<StateRevision>("sharedworlds.state-revision");

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
