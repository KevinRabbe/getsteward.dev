using System.Security.Cryptography;
using System.Text;

namespace SharedWorlds.Backend.Identity;

public enum FriendsBuildIdentityVerificationStatus
{
    Verified,
    InvalidCredential
}

public sealed record FriendsBuildIdentityVerificationResult(
    FriendsBuildIdentityVerificationStatus Status,
    VerifiedExternalIdentity? Identity);

/// <summary>
/// One explicitly provisioned private Friends Build identity. Only the SHA-256 digest of the
/// high-entropy bootstrap credential is configured; the backend does not need the plaintext secret.
/// </summary>
public sealed class FriendsBuildIdentityDefinition
{
    public FriendsBuildIdentityDefinition(
        string externalId,
        string displayName,
        string credentialSha256)
    {
        _ = new ExternalIdentityRef(FriendsBuildIdentityVerifier.Provider, externalId);
        if (string.IsNullOrWhiteSpace(displayName) ||
            displayName.Length > 128 ||
            displayName.Any(char.IsControl))
        {
            throw new ArgumentException(
                "Friends Build display name must be non-empty, contain no control characters, and be at most 128 characters.",
                nameof(displayName));
        }

        if (string.IsNullOrWhiteSpace(credentialSha256) || credentialSha256.Length != 64)
        {
            throw new ArgumentException(
                "Friends Build credential SHA-256 must contain exactly 64 hexadecimal characters.",
                nameof(credentialSha256));
        }

        byte[] credentialHash;
        try
        {
            credentialHash = Convert.FromHexString(credentialSha256);
        }
        catch (FormatException exception)
        {
            throw new ArgumentException(
                "Friends Build credential SHA-256 must contain exactly 64 hexadecimal characters.",
                nameof(credentialSha256),
                exception);
        }

        ExternalId = externalId;
        DisplayName = displayName;
        CredentialHash = credentialHash;
    }

    public string ExternalId { get; }
    public string DisplayName { get; }
    internal byte[] CredentialHash { get; }
}

/// <summary>
/// Small private identity verifier used only for explicitly configured Friends Build deployments.
/// It feeds the same VerifiedExternalIdentity -> StewardSessionService boundary as production Steam
/// authentication instead of creating a parallel account/session system.
/// </summary>
public sealed class FriendsBuildIdentityVerifier
{
    public const string Provider = "friends-build";
    public const int MaximumConfiguredIdentities = 64;

    private readonly FriendsBuildIdentityDefinition[]? _identities;
    private readonly string? _unavailableReason;

    public FriendsBuildIdentityVerifier(IEnumerable<FriendsBuildIdentityDefinition> identities)
    {
        ArgumentNullException.ThrowIfNull(identities);
        _identities = identities.ToArray();
        if (_identities.Length is < 1 or > MaximumConfiguredIdentities)
        {
            throw new ArgumentOutOfRangeException(
                nameof(identities),
                $"Friends Build requires between 1 and {MaximumConfiguredIdentities} configured identities when enabled.");
        }

        var externalIds = new HashSet<string>(StringComparer.Ordinal);
        var credentialHashes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var identity in _identities)
        {
            ArgumentNullException.ThrowIfNull(identity);
            if (!externalIds.Add(identity.ExternalId))
            {
                throw new ArgumentException(
                    "Friends Build external identity IDs must be unique.",
                    nameof(identities));
            }

            var hashKey = Convert.ToHexString(identity.CredentialHash);
            if (!credentialHashes.Add(hashKey))
            {
                throw new ArgumentException(
                    "Friends Build bootstrap credentials must be unique.",
                    nameof(identities));
            }
        }
    }

    private FriendsBuildIdentityVerifier(string unavailableReason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(unavailableReason);
        _unavailableReason = unavailableReason;
    }

    public static FriendsBuildIdentityVerifier CreateUnavailable(
        string unavailableReason = "Friends Build authentication is not configured on this deployment.")
        => new(unavailableReason);

    public FriendsBuildIdentityVerificationResult Verify(string credential)
    {
        if (_identities is null)
        {
            throw new ExternalIdentityProviderException(
                Provider,
                _unavailableReason ?? "Friends Build authentication is unavailable.",
                retryable: false);
        }

        if (!FriendsBuildCredential.IsValid(credential))
        {
            return new(FriendsBuildIdentityVerificationStatus.InvalidCredential, null);
        }

        var credentialHash = SHA256.HashData(Encoding.UTF8.GetBytes(credential));
        FriendsBuildIdentityDefinition? match = null;
        foreach (var candidate in _identities)
        {
            if (CryptographicOperations.FixedTimeEquals(credentialHash, candidate.CredentialHash))
            {
                match = candidate;
            }
        }

        if (match is null)
        {
            return new(FriendsBuildIdentityVerificationStatus.InvalidCredential, null);
        }

        return new(
            FriendsBuildIdentityVerificationStatus.Verified,
            new VerifiedExternalIdentity(
                new ExternalIdentityRef(Provider, match.ExternalId),
                match.DisplayName));
    }
}

/// <summary>
/// Friends Build bootstrap credential format/provisioning helper. A credential contains 256 random
/// bits and is intended to be entered once on a friend's machine, then replaced by normal Steward
/// access/refresh session credentials.
/// </summary>
public static class FriendsBuildCredential
{
    private const string Prefix = "st_friend_";
    private const int SecretBytes = 32;
    private const int EncodedSecretCharacters = 43;

    public static string Generate()
    {
        var bytes = RandomNumberGenerator.GetBytes(SecretBytes);
        return Prefix + Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    public static string HashForConfiguration(string credential)
    {
        if (!IsValid(credential))
        {
            throw new ArgumentException("Invalid Friends Build bootstrap credential.", nameof(credential));
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(credential)));
    }

    internal static bool IsValid(string credential)
    {
        if (string.IsNullOrWhiteSpace(credential) ||
            !credential.StartsWith(Prefix, StringComparison.Ordinal) ||
            credential.Length != Prefix.Length + EncodedSecretCharacters)
        {
            return false;
        }

        var encoded = credential.AsSpan(Prefix.Length);
        foreach (var value in encoded)
        {
            var valid = value is >= 'A' and <= 'Z' or
                >= 'a' and <= 'z' or
                >= '0' and <= '9' or
                '-' or '_';
            if (!valid)
            {
                return false;
            }
        }

        return true;
    }
}
