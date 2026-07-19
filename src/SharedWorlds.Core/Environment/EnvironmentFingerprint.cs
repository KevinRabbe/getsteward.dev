using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SharedWorlds.Core.Environment;

/// <summary>
/// Computes a temporary comparison fingerprint from a canonicalized environment manifest.
/// The manifest remains authoritative; this fingerprint is only a fast comparison aid.
/// </summary>
public static class EnvironmentFingerprint
{
    public static string Compute(EnvironmentManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        var canonical = new
        {
            manifest.SchemaVersion,
            manifest.AdapterId,
            manifest.GameVersion,
            Components = manifest.Components
                .OrderBy(x => x.Kind, StringComparer.Ordinal)
                .ThenBy(x => x.Id, StringComparer.Ordinal)
                .ThenBy(x => x.Version, StringComparer.Ordinal)
                .Select(x => new
                {
                    x.Kind,
                    x.Id,
                    x.Version,
                    x.Source,
                    Metadata = x.Metadata is null
                        ? null
                        : x.Metadata.OrderBy(kv => kv.Key, StringComparer.Ordinal)
                            .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal)
                }),
            Configuration = manifest.Configuration
                .OrderBy(kv => kv.Key, StringComparer.Ordinal)
                .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal)
        };

        var json = JsonSerializer.Serialize(canonical);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(json));
        return Convert.ToHexString(hash);
    }
}
