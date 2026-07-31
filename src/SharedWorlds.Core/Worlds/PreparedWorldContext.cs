using SharedWorlds.Core.Domain;

namespace SharedWorlds.Core.Worlds;

public sealed record PreparedWorldContext(World World, PreparedWorld PreparedWorld);
