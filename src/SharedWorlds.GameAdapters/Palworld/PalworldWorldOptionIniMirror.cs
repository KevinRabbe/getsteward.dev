using System.Globalization;
using System.Text;

namespace SharedWorlds.GameAdapters.Palworld;

internal sealed record PalworldWorldOptionIniMirrorResult(
    string Contents,
    IReadOnlyDictionary<string, string> SerializedValues,
    IReadOnlySet<string> ManagementOverrides);

/// <summary>
/// Acceptance-only projection of the read-only WorldOption settings snapshot into the
/// documented PalWorldSettings.ini OptionSettings syntax. It never writes WorldOption.sav.
/// Unknown/opaque property types fail closed rather than being guessed.
/// </summary>
internal static class PalworldWorldOptionIniMirror
{
    private static readonly HashSet<string> ManagementOverrideNames = new(StringComparer.Ordinal)
    {
        "AdminPassword",
        "RESTAPIEnabled",
        "RESTAPIPort"
    };

    public static PalworldWorldOptionIniMirrorResult Create(
        PalworldWorldOptionSettingsSnapshot snapshot,
        string transientAdminPassword,
        int restPort)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentException.ThrowIfNullOrWhiteSpace(transientAdminPassword);
        if (restPort is < 1 or > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(restPort));
        }

        var duplicate = snapshot.Settings
            .GroupBy(setting => setting.Name, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
        {
            throw new InvalidDataException(
                $"WorldOption settings contain duplicate property {duplicate.Key}; refusing to build an ambiguous INI mirror.");
        }

        var serialized = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var setting in snapshot.Settings)
        {
            if (ManagementOverrideNames.Contains(setting.Name))
            {
                continue;
            }

            serialized.Add(setting.Name, SerializeSetting(setting));
        }

        serialized["AdminPassword"] = QuoteIniString(transientAdminPassword);
        serialized["RESTAPIEnabled"] = "True";
        serialized["RESTAPIPort"] = restPort.ToString(CultureInfo.InvariantCulture);

        var optionSettings = string.Join(",", serialized.Select(pair => $"{pair.Key}={pair.Value}"));
        var contents = "[/Script/Pal.PalGameWorldSettings]" + Environment.NewLine +
            $"OptionSettings=({optionSettings})" + Environment.NewLine;

        return new PalworldWorldOptionIniMirrorResult(
            contents,
            serialized,
            new HashSet<string>(ManagementOverrideNames, StringComparer.Ordinal));
    }

    internal static string NormalizeExpectedRestValue(PalworldWorldOptionSetting setting)
    {
        ArgumentNullException.ThrowIfNull(setting);
        if (setting.Value is null)
        {
            throw new InvalidDataException(
                $"WorldOption setting {setting.Name} ({setting.PropertyType}) is opaque and cannot be REST-compared safely.");
        }

        return setting.PropertyType switch
        {
            "BoolProperty" => NormalizeBoolean(setting.Value, setting.Name),
            "IntProperty" or "Int64Property" or "UInt32Property" or "UInt64Property" =>
                NormalizeInteger(setting.Value, setting.Name),
            "FloatProperty" or "DoubleProperty" => NormalizeFloatingPoint(setting.Value, setting.Name),
            "StrProperty" or "NameProperty" => setting.Value,
            "EnumProperty" or "ByteProperty" => StripEnumPrefix(setting.Value),
            _ => throw new InvalidDataException(
                $"WorldOption setting {setting.Name} uses unsupported property type {setting.PropertyType}.")
        };
    }

    private static string SerializeSetting(PalworldWorldOptionSetting setting)
    {
        if (setting.Value is null)
        {
            throw new InvalidDataException(
                $"WorldOption setting {setting.Name} ({setting.PropertyType}) is opaque and cannot be mirrored safely.");
        }

        return setting.PropertyType switch
        {
            "BoolProperty" => NormalizeBoolean(setting.Value, setting.Name),
            "IntProperty" or "Int64Property" or "UInt32Property" or "UInt64Property" =>
                NormalizeInteger(setting.Value, setting.Name),
            "FloatProperty" or "DoubleProperty" => NormalizeFloatingPoint(setting.Value, setting.Name),
            "StrProperty" or "NameProperty" => QuoteIniString(setting.Value),
            "EnumProperty" or "ByteProperty" => SerializeEnum(setting),
            _ => throw new InvalidDataException(
                $"WorldOption setting {setting.Name} uses unsupported property type {setting.PropertyType}; refusing to guess its INI representation.")
        };
    }

    private static string SerializeEnum(PalworldWorldOptionSetting setting)
    {
        var value = StripEnumPrefix(setting.Value!);
        if (string.IsNullOrWhiteSpace(value) || value.IndexOfAny([',', '(', ')', '"', '\r', '\n']) >= 0)
        {
            throw new InvalidDataException(
                $"WorldOption setting {setting.Name} has an unsafe enum/byte value for PalWorldSettings.ini.");
        }

        return value;
    }

    private static string StripEnumPrefix(string value)
    {
        var separator = value.LastIndexOf("::", StringComparison.Ordinal);
        return separator >= 0 ? value[(separator + 2)..] : value;
    }

    private static string NormalizeBoolean(string value, string name)
    {
        if (bool.TryParse(value, out var parsed))
        {
            return parsed ? "True" : "False";
        }

        throw new InvalidDataException($"WorldOption setting {name} contains invalid Boolean value {value}.");
    }

    private static string NormalizeInteger(string value, string name)
    {
        if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var signed))
        {
            return signed.ToString(CultureInfo.InvariantCulture);
        }

        if (ulong.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var unsigned))
        {
            return unsigned.ToString(CultureInfo.InvariantCulture);
        }

        throw new InvalidDataException($"WorldOption setting {name} contains invalid integer value {value}.");
    }

    private static string NormalizeFloatingPoint(string value, string name)
    {
        if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) ||
            double.IsNaN(number) ||
            double.IsInfinity(number))
        {
            throw new InvalidDataException($"WorldOption setting {name} contains invalid floating-point value {value}.");
        }

        return number.ToString("R", CultureInfo.InvariantCulture);
    }

    private static string QuoteIniString(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Any(character => char.IsControl(character)))
        {
            throw new InvalidDataException("PalWorldSettings.ini string values cannot contain control characters in this acceptance mirror.");
        }

        var escaped = new StringBuilder(value.Length + 2);
        escaped.Append('"');
        foreach (var character in value)
        {
            if (character is '\\' or '"')
            {
                escaped.Append('\\');
            }

            escaped.Append(character);
        }

        escaped.Append('"');
        return escaped.ToString();
    }
}
