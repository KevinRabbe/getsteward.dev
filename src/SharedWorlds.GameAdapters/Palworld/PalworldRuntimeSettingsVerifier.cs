using System.Globalization;
using System.Text.Json;

namespace SharedWorlds.GameAdapters.Palworld;

internal sealed record PalworldRuntimeSettingsVerification(
    int RestPropertyCount,
    int VerifiedWorldSettingCount,
    IReadOnlyList<string> Mismatches,
    IReadOnlyList<string> UnexposedWorldSettings,
    IReadOnlyList<string> RestOnlySettings)
{
    public bool IsMatch =>
        RestPropertyCount > 0 &&
        VerifiedWorldSettingCount > 0 &&
        Mismatches.Count == 0 &&
        UnexposedWorldSettings.Count == 0;
}

/// <summary>
/// Compares Palworld's own REST-visible effective settings with the settings read from the
/// canonical WorldOption.sav. Representation differences are normalized only where they have
/// been observed and proven, including ordered simple arrays returned by REST as JSON arrays.
/// </summary>
internal static class PalworldRuntimeSettingsVerifier
{
    public static PalworldRuntimeSettingsVerification Verify(
        PalworldWorldOptionSettingsSnapshot snapshot,
        JsonElement restRoot,
        int restPort,
        IReadOnlySet<string> managementOverrides)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(managementOverrides);
        if (restRoot.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("Palworld /settings did not return a JSON object.");
        }

        var worldSettings = snapshot.Settings.ToDictionary(setting => setting.Name, StringComparer.Ordinal);
        var restProperties = restRoot.EnumerateObject().ToArray();
        var restNames = restProperties.Select(property => property.Name).ToHashSet(StringComparer.Ordinal);
        var mismatches = new List<string>();
        var unexposed = new List<string>();
        var verified = 0;

        foreach (var setting in snapshot.Settings)
        {
            if (setting.IsSensitive || managementOverrides.Contains(setting.Name))
            {
                continue;
            }

            if (!restRoot.TryGetProperty(setting.Name, out var actual))
            {
                unexposed.Add(setting.Name);
                continue;
            }

            if (RestValueMatches(setting, actual))
            {
                verified++;
            }
            else
            {
                mismatches.Add(setting.Name);
            }
        }

        if (restRoot.TryGetProperty("RESTAPIEnabled", out var restEnabled) &&
            restEnabled.ValueKind is not JsonValueKind.True)
        {
            mismatches.Add("RESTAPIEnabled(management override)");
        }

        if (restRoot.TryGetProperty("RESTAPIPort", out var restPortElement) &&
            (!TryGetJsonInteger(restPortElement, out var actualRestPort) || actualRestPort != restPort))
        {
            mismatches.Add("RESTAPIPort(management override)");
        }

        var restOnly = restNames
            .Where(name => !worldSettings.ContainsKey(name))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        return new PalworldRuntimeSettingsVerification(
            restProperties.Length,
            verified,
            mismatches.Distinct(StringComparer.Ordinal).OrderBy(name => name, StringComparer.Ordinal).ToArray(),
            unexposed.OrderBy(name => name, StringComparer.Ordinal).ToArray(),
            restOnly);
    }

    internal static bool RestValueMatches(PalworldWorldOptionSetting setting, JsonElement actual)
    {
        ArgumentNullException.ThrowIfNull(setting);
        var expected = PalworldWorldOptionIniMirror.NormalizeExpectedRestValue(setting);

        if (setting.PropertyType == "BoolProperty")
        {
            return bool.TryParse(expected, out var expectedBool) &&
                actual.ValueKind is JsonValueKind.True or JsonValueKind.False &&
                actual.GetBoolean() == expectedBool;
        }

        if (setting.PropertyType is "IntProperty" or "Int64Property" or "UInt32Property" or "UInt64Property")
        {
            return long.TryParse(expected, NumberStyles.Integer, CultureInfo.InvariantCulture, out var expectedSigned)
                ? TryGetJsonInteger(actual, out var actualInteger) && actualInteger == expectedSigned
                : ulong.TryParse(expected, NumberStyles.Integer, CultureInfo.InvariantCulture, out var expectedUnsigned) &&
                  actual.ValueKind == JsonValueKind.Number &&
                  actual.TryGetUInt64(out var actualUnsigned) &&
                  actualUnsigned == expectedUnsigned;
        }

        if (setting.PropertyType is "FloatProperty" or "DoubleProperty")
        {
            if (!double.TryParse(expected, NumberStyles.Float, CultureInfo.InvariantCulture, out var expectedNumber) ||
                actual.ValueKind != JsonValueKind.Number ||
                !actual.TryGetDouble(out var actualNumber))
            {
                return false;
            }

            var tolerance = 1e-6 * Math.Max(1d, Math.Abs(expectedNumber));
            return Math.Abs(actualNumber - expectedNumber) <= tolerance;
        }

        if (setting.PropertyType is "EnumProperty" or "ByteProperty")
        {
            if (actual.ValueKind == JsonValueKind.String)
            {
                return string.Equals(actual.GetString(), expected, StringComparison.Ordinal);
            }

            if (actual.ValueKind == JsonValueKind.Number &&
                long.TryParse(expected, NumberStyles.Integer, CultureInfo.InvariantCulture, out var expectedInteger))
            {
                return TryGetJsonInteger(actual, out var actualInteger) && actualInteger == expectedInteger;
            }

            return false;
        }

        if (setting.PropertyType == "ArrayProperty")
        {
            return SimpleArrayMatches(setting, actual);
        }

        return actual.ValueKind == JsonValueKind.String &&
            string.Equals(actual.GetString(), expected, StringComparison.Ordinal);
    }

    private static bool SimpleArrayMatches(PalworldWorldOptionSetting setting, JsonElement actual)
    {
        if (actual.ValueKind != JsonValueKind.Array || setting.Value is null)
        {
            return false;
        }

        var expected = ParseDecodedTuple(setting.Value);
        var actualItems = actual.EnumerateArray().ToArray();
        if (expected.Count != actualItems.Length)
        {
            return false;
        }

        for (var index = 0; index < expected.Count; index++)
        {
            if (actualItems[index].ValueKind != JsonValueKind.String ||
                !string.Equals(
                    actualItems[index].GetString(),
                    StripEnumPrefix(expected[index]),
                    StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private static IReadOnlyList<string> ParseDecodedTuple(string value)
    {
        if (value.Length < 2 || value[0] != '(' || value[^1] != ')')
        {
            throw new InvalidDataException("Decoded Palworld array setting is not a tuple.");
        }

        var inner = value[1..^1];
        return inner.Length == 0
            ? Array.Empty<string>()
            : inner.Split(',', StringSplitOptions.None).Select(item => item.Trim()).ToArray();
    }

    private static string StripEnumPrefix(string value)
    {
        var separator = value.LastIndexOf("::", StringComparison.Ordinal);
        return separator >= 0 ? value[(separator + 2)..] : value;
    }

    private static bool TryGetJsonInteger(JsonElement value, out long result)
    {
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out result))
        {
            return true;
        }

        result = default;
        return false;
    }
}
