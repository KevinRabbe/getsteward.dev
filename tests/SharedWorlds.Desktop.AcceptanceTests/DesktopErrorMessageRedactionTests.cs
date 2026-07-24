using Xunit;

namespace SharedWorlds.Desktop.AcceptanceTests;

public sealed class DesktopErrorMessageRedactionTests
{
    [Theory]
    [InlineData("Authorization: Bearer super-secret-ticket", "super-secret-ticket")]
    [InlineData("AdminPassword=hunter2", "hunter2")]
    [InlineData("joinToken=join-secret", "join-secret")]
    [InlineData("steamTicket=steam-secret", "steam-secret")]
    [InlineData("publisherApiKey=publisher-secret", "publisher-secret")]
    [InlineData("{\"accessToken\":\"json-secret\"}", "json-secret")]
    public void RedactRemovesKnownCredentialAssignments(string message, string secret)
    {
        var redacted = DesktopErrorMessage.Redact(message);

        Assert.DoesNotContain(secret, redacted, StringComparison.Ordinal);
        Assert.Contains("<redacted>", redacted, StringComparison.Ordinal);
    }

    [Fact]
    public void RedactRemovesPresignedUrlQueryButPreservesSafeObjectPath()
    {
        const string message =
            "GET https://objects.example/worlds/package?X-Amz-Credential=secret&X-Amz-Signature=signature#fragment failed";

        var redacted = DesktopErrorMessage.Redact(message);

        Assert.Contains("https://objects.example/worlds/package?<redacted>#fragment", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("secret", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("signature", redacted, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RedactLeavesOrdinaryDiagnosticTextUnchanged()
    {
        const string message = "World restore failed because payload.bin is missing.";

        Assert.Equal(message, DesktopErrorMessage.Redact(message));
    }
}
