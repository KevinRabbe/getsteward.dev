namespace SharedWorlds.Core.Domain;

public readonly record struct WorldId(Guid Value)
{
    public static WorldId New() => new(Guid.NewGuid());
    public override string ToString() => Value.ToString("N");
}

public readonly record struct RevisionId(Guid Value)
{
    public static RevisionId New() => new(Guid.NewGuid());
    public override string ToString() => Value.ToString("N");
}

public sealed record UserIdentity(string Provider, string ExternalId, string? DisplayName = null);
