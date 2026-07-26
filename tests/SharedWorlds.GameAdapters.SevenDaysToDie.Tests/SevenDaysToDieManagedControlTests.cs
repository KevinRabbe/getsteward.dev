using System.Text;
using System.Xml.Linq;

namespace SharedWorlds.GameAdapters.SevenDaysToDie.Tests;

public sealed class SevenDaysToDieManagedControlTests
{
    private readonly string _userDataDirectory = Path.GetFullPath(Path.Combine(
        Path.GetTempPath(),
        $"sharedworlds-7dtd-control-{Guid.NewGuid():N}",
        "user-data"));

    [Fact]
    public void ManagedHostTransformOwnsTelnetCoordinatesAndPreservesSandboxCode()
    {
        const string password = "0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF";
        var source = Utf8(
            """
            <ServerSettings>
                <property name="GameWorld" value="Navezgane" />
                <property name="GameName" value="Old Save" />
                <property name="SandboxCode" value="CUSTOM-SANDBOX-CODE" />
                <property name="TelnetEnabled" value="false" />
                <property name="TelnetPort" value="8081" />
                <property name="TelnetPassword" value="old-password" />
            </ServerSettings>
            """);
        var original = source.ToArray();
        var control = new SevenDaysToDieManagedControl(39123, password);

        var transformed = SevenDaysToDieManagedServerConfiguration.TransformForManagedHost(
            source,
            _userDataDirectory,
            "Steward World",
            "Steward Save",
            control);

        Assert.Equal(original, source);
        var document = Parse(transformed);
        Assert.Equal("CUSTOM-SANDBOX-CODE", PropertyValue(document, "SandboxCode"));
        Assert.Equal("true", PropertyValue(document, "TelnetEnabled"));
        Assert.Equal("39123", PropertyValue(document, "TelnetPort"));
        Assert.Equal(password, PropertyValue(document, "TelnetPassword"));
    }

    [Fact]
    public void ManagedHostTransformAddsMissingTelnetProperties()
    {
        const string password = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
        var source = Utf8(
            """
            <ServerSettings>
                <property name="GameWorld" value="Navezgane" />
                <property name="GameName" value="Managed Save" />
            </ServerSettings>
            """);

        var transformed = SevenDaysToDieManagedServerConfiguration.TransformForManagedHost(
            source,
            _userDataDirectory,
            "Navezgane",
            "Managed Save",
            new SevenDaysToDieManagedControl(42001, password));

        var document = Parse(transformed);
        Assert.Equal("true", PropertyValue(document, "TelnetEnabled"));
        Assert.Equal("42001", PropertyValue(document, "TelnetPort"));
        Assert.Equal(password, PropertyValue(document, "TelnetPassword"));
        Assert.Single(ActiveProperties(document, "TelnetEnabled"));
        Assert.Single(ActiveProperties(document, "TelnetPort"));
        Assert.Single(ActiveProperties(document, "TelnetPassword"));
    }

    [Fact]
    public void GenericTransformDoesNotTakeOwnershipOfExistingTelnetSettings()
    {
        var source = Utf8(
            """
            <ServerSettings>
                <property name="GameWorld" value="Navezgane" />
                <property name="GameName" value="Managed Save" />
                <property name="TelnetEnabled" value="false" />
                <property name="TelnetPort" value="18081" />
                <property name="TelnetPassword" value="leave-me-alone" />
            </ServerSettings>
            """);

        var transformed = SevenDaysToDieManagedServerConfiguration.Transform(
            source,
            _userDataDirectory,
            "Navezgane",
            "Managed Save");

        var document = Parse(transformed);
        Assert.Equal("false", PropertyValue(document, "TelnetEnabled"));
        Assert.Equal("18081", PropertyValue(document, "TelnetPort"));
        Assert.Equal("leave-me-alone", PropertyValue(document, "TelnetPassword"));
    }

    [Fact]
    public void DuplicateTelnetPropertyIsRejectedBeforeManagedSecretCanBeApplied()
    {
        const string password = "BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB";
        var source = Utf8(
            """
            <ServerSettings>
                <property name="GameWorld" value="Navezgane" />
                <property name="GameName" value="Managed Save" />
                <property name="TelnetPort" value="8081" />
                <property name="TelnetPort" value="8082" />
            </ServerSettings>
            """);

        var exception = Assert.Throws<InvalidDataException>(() =>
            SevenDaysToDieManagedServerConfiguration.TransformForManagedHost(
                source,
                _userDataDirectory,
                "Navezgane",
                "Managed Save",
                new SevenDaysToDieManagedControl(42002, password)));

        Assert.Contains("more than one active TelnetPort", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(password, Encoding.UTF8.GetString(source), StringComparison.Ordinal);
    }

    [Fact]
    public void ManagedControlValidatesAndRedactsItsSecret()
    {
        const string password = "CDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789AB";
        var control = new SevenDaysToDieManagedControl(42003, password);

        Assert.Equal(42003, control.Port);
        Assert.Equal(password, control.Password);
        Assert.DoesNotContain(password, control.ToString(), StringComparison.Ordinal);
        Assert.Contains("<redacted>", control.ToString(), StringComparison.Ordinal);
        Assert.Throws<ArgumentOutOfRangeException>(() => new SevenDaysToDieManagedControl(0, password));
        Assert.Throws<ArgumentException>(() => new SevenDaysToDieManagedControl(42003, "not-a-managed-secret"));
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
