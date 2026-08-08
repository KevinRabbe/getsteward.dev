using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace SharedWorlds.Core.Domain;

/// <summary>
/// Computes a deterministic SHA-256 fingerprint over a set of stable external identities. Display
/// names are deliberately excluded. Provider comparison is case-insensitive everywhere else in the
/// World model, so providers are normalized to lower invariant while external IDs remain exact.
/// Length-prefixed UTF-8 fields avoid delimiter ambiguity.
/// </summary>
public static class StableIdentitySetFingerprint
{
    public static string Compute(IEnumerable<UserIdentity> identities)
    {
        ArgumentNullException.ThrowIfNull(identities);
        return Compute(identities.Select(static identity =>
        {
            ArgumentNullException.ThrowIfNull(identity);
            return (identity.Provider, identity.ExternalId);
        }));
    }

    public static string Compute(IEnumerable<(string Provider, string ExternalId)> identities)
    {
        ArgumentNullException.ThrowIfNull(identities);
        var canonical = identities
            .Select(static identity => Canonicalize(identity.Provider, identity.ExternalId))
            .Distinct()
            .OrderBy(static identity => identity.Provider, StringComparer.Ordinal)
            .ThenBy(static identity => identity.ExternalId, StringComparer.Ordinal)
            .ToArray();

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> countBuffer = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(countBuffer, canonical.Length);
        hash.AppendData(countBuffer);

        foreach (var identity in canonical)
        {
            AppendField(hash, identity.Provider);
            AppendField(hash, identity.ExternalId);
        }

        return Convert.ToHexString(hash.GetHashAndReset());
    }

    public static bool IsCanonicalFingerprint(string? value)
        => value is { Length: 64 } && value.All(static character =>
            character is >= '0' and <= '9' or >= 'A' and <= 'F');

    private static (string Provider, string ExternalId) Canonicalize(
        string provider,
        string externalId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(provider);
        ArgumentException.ThrowIfNullOrWhiteSpace(externalId);
        return (provider.ToLowerInvariant(), externalId);
    }

    private static void AppendField(IncrementalHash hash, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        Span<byte> lengthBuffer = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(lengthBuffer, bytes.Length);
        hash.AppendData(lengthBuffer);
        hash.AppendData(bytes);
    }
}
