namespace SharedWorlds.Backend.Identity;

public sealed record ExternalIdentityRef
{
    private const int MaximumProviderCharacters = 128;
    private const int MaximumExternalIdCharacters = 512;

    public ExternalIdentityRef(string provider, string externalId)
    {
        ValidatePart(provider, MaximumProviderCharacters, "Identity provider", nameof(provider));
        ValidatePart(externalId, MaximumExternalIdCharacters, "External identity ID", nameof(externalId));
        Provider = provider;
        ExternalId = externalId;
    }

    public string Provider { get; }
    public string ExternalId { get; }

    private static void ValidatePart(
        string value,
        int maximumCharacters,
        string fieldName,
        string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            value.Length > maximumCharacters ||
            value.Any(char.IsControl))
        {
            throw new ArgumentException(
                $"{fieldName} must be non-empty, contain no control characters, and be at most {maximumCharacters} characters.",
                parameterName);
        }
    }
}

/// <summary>
/// Represents an identity whose external authentication proof has already been verified by a
/// trusted backend verifier. Client-supplied provider/ID strings must never be treated as this type
/// until verification succeeds.
/// </summary>
public sealed record VerifiedExternalIdentity
{
    private const int MaximumDisplayNameCharacters = 512;

    public VerifiedExternalIdentity(
        ExternalIdentityRef subject,
        string? displayName = null)
    {
        ArgumentNullException.ThrowIfNull(subject);
        if (displayName is not null &&
            (displayName.Length > MaximumDisplayNameCharacters || displayName.Any(char.IsControl)))
        {
            throw new ArgumentException(
                $"Verified display name must contain no control characters and be at most {MaximumDisplayNameCharacters} characters.",
                nameof(displayName));
        }

        Subject = subject;
        DisplayName = string.IsNullOrWhiteSpace(displayName) ? null : displayName;
    }

    public ExternalIdentityRef Subject { get; }
    public string? DisplayName { get; }
}
