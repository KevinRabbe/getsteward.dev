using System.Globalization;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace SharedWorlds.GameAdapters.SevenDaysToDie;

internal static class SevenDaysToDieManagedServerConfiguration
{
    internal const int MaximumConfigurationBytes = 4 * 1024 * 1024;
    private static readonly byte[] Utf8Bom = [0xEF, 0xBB, 0xBF];
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    internal static byte[] Transform(
        byte[] sourceBytes,
        string userDataDirectory,
        string worldName,
        string gameName)
        => TransformCore(
            sourceBytes,
            userDataDirectory,
            worldName,
            gameName,
            managedControl: null);

    internal static byte[] TransformForManagedHost(
        byte[] sourceBytes,
        string userDataDirectory,
        string worldName,
        string gameName,
        SevenDaysToDieManagedControl managedControl)
    {
        ArgumentNullException.ThrowIfNull(managedControl);
        return TransformCore(
            sourceBytes,
            userDataDirectory,
            worldName,
            gameName,
            managedControl);
    }

    private static byte[] TransformCore(
        byte[] sourceBytes,
        string userDataDirectory,
        string worldName,
        string gameName,
        SevenDaysToDieManagedControl? managedControl)
    {
        ArgumentNullException.ThrowIfNull(sourceBytes);
        ArgumentException.ThrowIfNullOrWhiteSpace(userDataDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(worldName);
        ArgumentException.ThrowIfNullOrWhiteSpace(gameName);

        if (sourceBytes.Length > MaximumConfigurationBytes)
        {
            throw new InvalidDataException(
                $"7 Days to Die server configuration exceeds Steward's {MaximumConfigurationBytes}-byte management safety limit.");
        }

        var hasBom = sourceBytes.AsSpan().StartsWith(Utf8Bom);
        var contentBytes = hasBom ? sourceBytes.AsSpan(Utf8Bom.Length) : sourceBytes.AsSpan();

        string sourceText;
        try
        {
            sourceText = StrictUtf8.GetString(contentBytes);
        }
        catch (DecoderFallbackException exception)
        {
            throw new InvalidDataException(
                "7 Days to Die server configuration is not valid UTF-8; Steward will not transform unknown configuration bytes.",
                exception);
        }

        XDocument document;
        try
        {
            using var textReader = new StringReader(sourceText);
            using var xmlReader = XmlReader.Create(
                textReader,
                new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
            document = XDocument.Load(xmlReader, LoadOptions.PreserveWhitespace);
        }
        catch (XmlException exception)
        {
            throw new InvalidDataException(
                "7 Days to Die server configuration is not valid XML.",
                exception);
        }

        var root = document.Root;
        if (root is null || !string.Equals(root.Name.LocalName, "ServerSettings", StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "7 Days to Die server configuration must contain one ServerSettings root element.");
        }

        ValidateUtf8Declaration(document);

        var fullUserDataDirectory = Path.GetFullPath(userDataDirectory);
        SetRequiredProperty(root, "GameWorld", worldName);
        SetRequiredProperty(root, "GameName", gameName);
        SetOwnedProperty(root, "UserDataFolder", fullUserDataDirectory);
        SetOwnedProperty(root, "SaveGameFolder", Path.Combine(fullUserDataDirectory, "Saves"));
        ValidateOptionalUnambiguousProperty(root, "SandboxCode");

        if (managedControl is not null)
        {
            SetOwnedProperty(root, "TelnetEnabled", "true");
            SetOwnedProperty(
                root,
                "TelnetPort",
                managedControl.Port.ToString(CultureInfo.InvariantCulture));
            SetOwnedProperty(root, "TelnetPassword", managedControl.Password);
        }

        var transformedContent = StrictUtf8.GetBytes(document.ToString(SaveOptions.DisableFormatting));
        return hasBom ? Combine(Utf8Bom, transformedContent) : transformedContent;
    }

    private static void SetRequiredProperty(XElement root, string name, string value)
    {
        var property = FindSingleProperty(root, name)
            ?? throw new InvalidDataException(
                $"7 Days to Die server configuration must contain exactly one active {name} property; found 0.");
        var valueAttribute = property.Attribute("value")
            ?? throw new InvalidDataException(
                $"7 Days to Die server configuration property {name} has no value attribute.");
        valueAttribute.Value = value;
    }

    private static void SetOwnedProperty(XElement root, string name, string value)
    {
        var property = FindSingleProperty(root, name);
        if (property is null)
        {
            root.Add(new XElement("property", new XAttribute("name", name), new XAttribute("value", value)));
            return;
        }

        property.SetAttributeValue("value", value);
    }

    private static void ValidateOptionalUnambiguousProperty(XElement root, string name)
    {
        var property = FindSingleProperty(root, name);
        if (property is not null && property.Attribute("value") is null)
        {
            throw new InvalidDataException(
                $"7 Days to Die server configuration property {name} has no value attribute.");
        }
    }

    private static XElement? FindSingleProperty(XElement root, string name)
    {
        var matches = root
            .Elements()
            .Where(element =>
                string.Equals(element.Name.LocalName, "property", StringComparison.Ordinal) &&
                string.Equals(element.Attribute("name")?.Value, name, StringComparison.Ordinal))
            .Take(2)
            .ToArray();
        if (matches.Length > 1)
        {
            throw new InvalidDataException(
                $"7 Days to Die server configuration contains more than one active {name} property.");
        }

        return matches.SingleOrDefault();
    }

    private static void ValidateUtf8Declaration(XDocument document)
    {
        var encoding = document.Declaration?.Encoding;
        if (string.IsNullOrWhiteSpace(encoding) ||
            string.Equals(encoding, "utf-8", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(encoding, "utf8", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        throw new InvalidDataException(
            $"7 Days to Die server configuration declares unsupported encoding '{encoding}'. Steward requires UTF-8 configuration bytes.");
    }

    private static byte[] Combine(byte[] prefix, byte[] content)
    {
        var result = new byte[prefix.Length + content.Length];
        prefix.CopyTo(result, 0);
        content.CopyTo(result, prefix.Length);
        return result;
    }
}
