namespace SharedWorlds.Backend.Identity;

public sealed record ExternalIdentityRef
{
    public ExternalIdentityRef(string provider, string externalId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(provider);
        ArgumentException.ThrowIfNullOrWhiteSpace(externalId);
        Provider = provider;
        ExternalId = externalId;
    }

    public string Provider { get; }
    public string ExternalId { get; }
}

/// <summary>
/// Represents an identity whose external authentication proof has already been verified by a
/// trusted backend verifier. Client-supplied provider/ID strings must never be treated as this type
/// until verification succeeds.
/// </summary>
public sealed record VerifiedExternalIdentity
{
    public VerifiedExternalIdentity(
        ExternalIdentityRef subject,
        string? displayName = null)
    {
        ArgumentNullException.ThrowIfNull(subject);
        Subject = subject;
        DisplayName = string.IsNullOrWhiteSpace(displayName) ? null : displayName;
    }

    public ExternalIdentityRef Subject { get; }
    public string? DisplayName { get; }
}
