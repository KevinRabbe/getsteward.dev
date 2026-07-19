using SharedWorlds.Core.Domain;

namespace SharedWorlds.Core.Errors;

public abstract class SharedWorldsException : Exception
{
    protected SharedWorldsException(string message)
        : base(message)
    {
    }
}

public sealed class WorldNotFoundException : SharedWorldsException
{
    public WorldNotFoundException(WorldId worldId)
        : base($"World '{worldId}' does not exist.")
    {
        WorldId = worldId;
    }

    public WorldId WorldId { get; }
}

public sealed class WorldIntegrityException : SharedWorldsException
{
    public WorldIntegrityException(WorldId worldId, string problem)
        : base($"World '{worldId}' is incomplete or inconsistent: {problem}")
    {
        WorldId = worldId;
        Problem = problem;
    }

    public WorldId WorldId { get; }
    public string Problem { get; }
}

public sealed class RevisionNotFoundException : SharedWorldsException
{
    public RevisionNotFoundException(
        WorldId worldId,
        RevisionId revisionId,
        string revisionKind)
        : base($"{revisionKind} revision '{revisionId}' for World '{worldId}' does not exist.")
    {
        WorldId = worldId;
        RevisionId = revisionId;
        RevisionKind = revisionKind;
    }

    public WorldId WorldId { get; }
    public RevisionId RevisionId { get; }
    public string RevisionKind { get; }
}

public sealed class AdapterMismatchException : SharedWorldsException
{
    public AdapterMismatchException(
        string expectedAdapterId,
        string actualAdapterId,
        string mismatchContext)
        : base($"The {mismatchContext} belongs to adapter '{actualAdapterId}', not '{expectedAdapterId}'.")
    {
        ExpectedAdapterId = expectedAdapterId;
        ActualAdapterId = actualAdapterId;
        MismatchContext = mismatchContext;
    }

    public string ExpectedAdapterId { get; }
    public string ActualAdapterId { get; }
    public string MismatchContext { get; }
}

public sealed class PersistedDataCompatibilityException : SharedWorldsException
{
    public PersistedDataCompatibilityException(
        string documentType,
        int encounteredSchemaVersion,
        int currentSchemaVersion)
        : base(
            $"Persisted document '{documentType}' uses schema version {encounteredSchemaVersion}, " +
            $"but this build supports version {currentSchemaVersion} and has no migration path.")
    {
        DocumentType = documentType;
        EncounteredSchemaVersion = encounteredSchemaVersion;
        CurrentSchemaVersion = currentSchemaVersion;
    }

    public string DocumentType { get; }
    public int EncounteredSchemaVersion { get; }
    public int CurrentSchemaVersion { get; }
}

public sealed class WorldSharingRequiredException : SharedWorldsException
{
    public WorldSharingRequiredException(WorldId worldId)
        : base($"World '{worldId}' is local-only and cannot be hosted or joined until sharing is explicitly enabled.")
    {
        WorldId = worldId;
    }

    public WorldId WorldId { get; }
}

public sealed class WorldSessionConflictException : SharedWorldsException
{
    public WorldSessionConflictException(WorldId worldId, string message)
        : base($"World '{worldId}': {message}")
    {
        WorldId = worldId;
    }

    public WorldId WorldId { get; }
}
