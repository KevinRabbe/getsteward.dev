using System.Text;
using System.Xml.Linq;

namespace SharedWorlds.GameAdapters.SevenDaysToDie.Tests;

public sealed class SevenDaysToDieManagedServerConfigurationTests
{
    private readonly string _userDataDirectory = Path.GetFullPath(Path.Combine(
        Path.GetTempPath(),
        $"sharedworlds-7dtd-managed-config-{Guid.NewGuid():N}",
        "user-data"));

    [Fact]
    public void TransformBindsWorldAndStorageWhilePreservingSandboxCode()
    {
        var source = Utf8(
            """
            <?xml version="1.0" encoding="utf-8"?>
            <ServerSettings>
                <property name="ServerName" value="Keep Me" />
                <property name="GameWorld" value="Navezgane" />
                <property name="GameName" value="Old Save" />
                <property name="SandboxCode" value="AAAJABJACJADJARFBNC" />
                <property name="UserDataFolder" value="C:\outside" />
                <property name="SaveGameFolder" value="D:\outside\Saves" />
            </ServerSettings>
            """);
        var original = source.ToArray();

        var transformed = SevenDaysToDieManagedServerConfiguration.Transform(
            source,
            _userDataDirectory,
            "Steward World",
            "Steward Save");

        Assert.Equal(original, source);
        var document = Parse(transformed);
        Assert.Equal("Keep Me", PropertyValue(document, "ServerName"));
        Assert.Equal("Steward World", PropertyValue(document, "GameWorld"));
        Assert.Equal("Steward Save", PropertyValue(document, "GameName"));
        Assert.Equal("AAAJABJACJADJARFBNC", PropertyValue(document, "SandboxCode"));
        Assert.Equal(_userDataDirectory, PropertyValue(document, "UserDataFolder"));
        Assert.Equal(
            Path.Combine(_userDataDirectory, "Saves"),
            PropertyValue(document, "SaveGameFolder"));
    }

    [Fact]
    public void CommentedFolderExamplesDoNotCountAsActiveOwnershipProperties()
    {
        var source = Utf8(
            """
            <ServerSettings>
                <!-- <property name="UserDataFolder" value="absolute path" /> -->
                <!-- <property name="SaveGameFolder" value="absolute path" /> -->
                <property name="GameWorld" value="Navezgane" />
                <property name="GameName" value="Old Save" />
            </ServerSettings>
            """);

        var transformed = SevenDaysToDieManagedServerConfiguration.Transform(
            source,
            _userDataDirectory,
            "Navezgane",
            "Managed Save");

        var document = Parse(transformed);
        Assert.Equal(_userDataDirectory, PropertyValue(document, "UserDataFolder"));
        Assert.Equal(
            Path.Combine(_userDataDirectory, "Saves"),
            PropertyValue(document, "SaveGameFolder"));
        Assert.Single(ActiveProperties(document, "UserDataFolder"));
        Assert.Single(ActiveProperties(document, "SaveGameFolder"));
    }

    [Fact]
    public void DuplicateOwnedPropertyIsRejected()
    {
        var source = Utf8(
            """
            <ServerSettings>
                <property name="GameWorld" value="Navezgane" />
                <property name="GameName" value="First" />
                <property name="GameName" value="Second" />
            </ServerSettings>
            """);

        var exception = Assert.Throws<InvalidDataException>(() =>
            SevenDaysToDieManagedServerConfiguration.Transform(
                source,
                _userDataDirectory,
                "Navezgane",
                "Managed Save"));

        Assert.Contains("more than one active GameName", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void DuplicateSandboxCodeIsRejectedWithoutInterpretingEitherValue()
    {
        var source = Utf8(
            """
            <ServerSettings>
                <property name="GameWorld" value="Navezgane" />
                <property name="GameName" value="Managed Save" />
                <property name="SandboxCode" value="FIRST" />
                <property name="SandboxCode" value="SECOND" />
            </ServerSettings>
            """);

        var exception = Assert.Throws<InvalidDataException>(() =>
            SevenDaysToDieManagedServerConfiguration.Transform(
                source,
                _userDataDirectory,
                "Navezgane",
                "Managed Save"));

        Assert.Contains("more than one active SandboxCode", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void MissingRequiredWorldPropertyIsRejected()
    {
        var source = Utf8(
            """
            <ServerSettings>
                <property name="GameWorld" value="Navezgane" />
            </ServerSettings>
            """);

        var exception = Assert.Throws<InvalidDataException>(() =>
            SevenDaysToDieManagedServerConfiguration.Transform(
                source,
                _userDataDirectory,
                "Navezgane",
                "Managed Save"));

        Assert.Contains("exactly one active GameName property; found 0", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void OversizedConfigurationIsRejectedBeforeXmlParsing()
    {
        var source = new byte[SevenDaysToDieManagedServerConfiguration.MaximumConfigurationBytes + 1];

        var exception = Assert.Throws<InvalidDataException>(() =>
            SevenDaysToDieManagedServerConfiguration.Transform(
                source,
                _userDataDirectory,
                "Navezgane",
                "Managed Save"));

        Assert.Contains("management safety limit", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void InvalidUtf8IsRejected()
    {
        byte[] source = [0xC3, 0x28];

        var exception = Assert.Throws<InvalidDataException>(() =>
            SevenDaysToDieManagedServerConfiguration.Transform(
                source,
                _userDataDirectory,
                "Navezgane",
                "Managed Save"));

        Assert.Contains("not valid UTF-8", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Utf8BomIsPreserved()
    {
        var content = Utf8(
            """
            <ServerSettings>
                <property name="GameWorld" value="Navezgane" />
                <property name="GameName" value="Managed Save" />
            </ServerSettings>
            """);
        byte[] source = [0xEF, 0xBB, 0xBF, .. content];

        var transformed = SevenDaysToDieManagedServerConfiguration.Transform(
            source,
            _userDataDirectory,
            "Navezgane",
            "Managed Save");

        Assert.Equal((byte)0xEF, transformed[0]);
        Assert.Equal((byte)0xBB, transformed[1]);
        Assert.Equal((byte)0xBF, transformed[2]);
    }

    private static byte[] Utf8(string value)
        => Encoding.UTF8.GetBytes(value);

    private static XDocument Parse(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        return XDocument.Load(stream, LoadOptions.PreserveWhitespace);
    }

    private static string PropertyValue(XDocument document, string name)
        => Assert.Single(ActiveProperties(document, name)).Attribute("value")?.Value
            ?? throw new Xunit.Sdk.XunitException($"Property {name} has no value attribute.");

    private static IEnumerable<XElement> ActiveProperties(XDocument document, string name)
        => document.Root!
            .Elements("property")
            .Where(element => string.Equals(element.Attribute("name")?.Value, name, StringComparison.Ordinal));
}
