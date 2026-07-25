using System.Text;

namespace SharedWorlds.GameAdapters.ProjectZomboid.Tests;

public sealed class ProjectZomboidTransientManagementConfigurationTests
{
    private const string Password = "0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF";

    [Fact]
    public void CreateInjectsOnlyTransientPasswordAndPreservesPortAndOtherSettings()
    {
        const string source =
            "PublicName=Steward\r\n" +
            "Password=join-secret\r\n" +
            "RCONPort=27015\r\n" +
            "RCONPassword=\r\n" +
            "DiscordToken=\r\n";
        var sourceBytes = Encoding.UTF8.GetBytes(source);

        var configuration = ProjectZomboidTransientManagementConfigurationBuilder.Create(
            sourceBytes,
            Password);
        var runtimeText = Encoding.UTF8.GetString(configuration.RuntimeBytes);

        Assert.Equal(27015, configuration.Port);
        Assert.Equal(sourceBytes, configuration.OriginalBytes);
        Assert.Equal(sourceBytes, Encoding.UTF8.GetBytes(source));
        Assert.Contains("Password=join-secret\r\n", runtimeText, StringComparison.Ordinal);
        Assert.Contains("RCONPort=27015\r\n", runtimeText, StringComparison.Ordinal);
        Assert.Contains($"RCONPassword={Password}\r\n", runtimeText, StringComparison.Ordinal);
        Assert.Contains("DiscordToken=\r\n", runtimeText, StringComparison.Ordinal);
    }

    [Fact]
    public void CreatePreservesUtf8BomAndTrailingNewline()
    {
        const string source = "RCONPort=16262\nRCONPassword=\n";
        var content = Encoding.UTF8.GetBytes(source);
        var sourceBytes = new byte[3 + content.Length];
        sourceBytes[0] = 0xEF;
        sourceBytes[1] = 0xBB;
        sourceBytes[2] = 0xBF;
        content.CopyTo(sourceBytes, 3);

        var configuration = ProjectZomboidTransientManagementConfigurationBuilder.Create(
            sourceBytes,
            Password);

        Assert.True(configuration.RuntimeBytes.AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF }));
        Assert.EndsWith("\n", Encoding.UTF8.GetString(configuration.RuntimeBytes), StringComparison.Ordinal);
        Assert.Equal(16262, configuration.Port);
    }

    [Fact]
    public void ExistingManagementPasswordIsRejectedInsteadOfOverwritten()
    {
        var bytes = Encoding.UTF8.GetBytes(
            "RCONPort=27015\nRCONPassword=existing-secret\n");

        var exception = Assert.Throws<InvalidDataException>(() =>
            ProjectZomboidTransientManagementConfigurationBuilder.Create(bytes, Password));

        Assert.Contains("will not overwrite", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("existing-secret", exception.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("RCONPassword=\n")]
    [InlineData("RCONPort=27015\n")]
    [InlineData("RCONPort=27015\nRCONPort=27016\nRCONPassword=\n")]
    [InlineData("RCONPort=27015\nRCONPassword=\nRCONPassword=\n")]
    public void MissingOrDuplicateManagementEntriesAreRejected(string source)
    {
        var exception = Assert.Throws<InvalidDataException>(() =>
            ProjectZomboidTransientManagementConfigurationBuilder.Create(
                Encoding.UTF8.GetBytes(source),
                Password));

        Assert.Contains("exactly one", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("65536")]
    [InlineData("not-a-port")]
    [InlineData(" 27015 ")]
    public void InvalidOrNonCanonicalPortIsRejected(string port)
    {
        var source = $"RCONPort={port}\nRCONPassword=\n";

        Assert.Throws<InvalidDataException>(() =>
            ProjectZomboidTransientManagementConfigurationBuilder.Create(
                Encoding.UTF8.GetBytes(source),
                Password));
    }

    [Theory]
    [InlineData("short")]
    [InlineData("GGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGG")]
    public void NonStewardPasswordShapeIsRejected(string password)
    {
        var source = Encoding.UTF8.GetBytes("RCONPort=27015\nRCONPassword=\n");

        Assert.Throws<ArgumentException>(() =>
            ProjectZomboidTransientManagementConfigurationBuilder.Create(source, password));
    }

    [Fact]
    public void GeneratedPasswordIs256BitHexWithoutEmbeddingOtherState()
    {
        var password = ProjectZomboidTransientManagementConfigurationBuilder.CreateTransientPassword();

        Assert.Equal(64, password.Length);
        Assert.All(password, character => Assert.True(Uri.IsHexDigit(character)));
    }

    [Fact]
    public void OversizedConfigurationIsRejected()
    {
        var source = new byte[(4 * 1024 * 1024) + 1];

        Assert.Throws<InvalidDataException>(() =>
            ProjectZomboidTransientManagementConfigurationBuilder.Create(source, Password));
    }
}
