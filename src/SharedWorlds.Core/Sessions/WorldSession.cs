using SharedWorlds.Core.Domain;

namespace SharedWorlds.Core.Sessions;

public sealed record WorldSession(
    WorldId WorldId,
    SessionState State,
    UserIdentity? ActiveHost,
    DateTimeOffset UpdatedAt);
