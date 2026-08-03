using System.Reflection;
using SharedWorlds.Infrastructure.Remote;
using Xunit;

namespace SharedWorlds.Infrastructure.Tests;

public sealed class StewardPrivateSnapshotTransferHeaderPolicyTests
{
    [Theory]
    [InlineData("Authorization")]
    [InlineData("Connection")]
    [InlineData("Content-Length")]
    [InlineData("Host")]
    [InlineData("Proxy-Authorization")]
    [InlineData("Range")]
    [InlineData("TE")]
    [InlineData("Trailer")]
    [InlineData("Transfer-Encoding")]
    [InlineData("Upgrade")]
    public void RejectsAuthorityFramingAndRangeHeaders(string headerName)
    {
        var exception = InvokeValidateHeaders(new Dictionary<string, string>
        {
            [headerName] = "untrusted"
        });

        var invalid = Assert.IsType<InvalidDataException>(exception.InnerException);
        Assert.Contains("forbidden header", invalid.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AcceptsBoundedProviderMetadataHeaders()
    {
        var method = GetValidateHeaders();
        var exception = Record.Exception(() => method.Invoke(
            null,
            new object[]
            {
                new Dictionary<string, string>
                {
                    ["x-amz-checksum-sha256"] = "abc123",
                    ["x-amz-server-side-encryption"] = "AES256"
                }
            }));

        Assert.Null(exception);
    }

    private static TargetInvocationException InvokeValidateHeaders(
        IReadOnlyDictionary<string, string> headers)
    {
        var method = GetValidateHeaders();
        return Assert.Throws<TargetInvocationException>(() => method.Invoke(
            null,
            new object[] { headers }));
    }

    private static MethodInfo GetValidateHeaders()
        => typeof(StewardPrivateSnapshotTransferClient).GetMethod(
               "ValidateHeaders",
               BindingFlags.NonPublic | BindingFlags.Static)
           ?? throw new InvalidOperationException(
               "Private snapshot transfer header validation method was not found.");
}
