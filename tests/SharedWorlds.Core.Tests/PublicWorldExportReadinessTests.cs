using SharedWorlds.Core.Abstractions;
using Xunit;

namespace SharedWorlds.Core.Tests;

public sealed class PublicWorldExportReadinessTests
{
    [Fact]
    public void SupportedCarriesNoInventedReason()
    {
        var readiness = PublicWorldExportReadiness.Supported();

        Assert.True(readiness.IsSupported);
        Assert.Null(readiness.Reason);
    }

    [Fact]
    public void UnsupportedRejectsNullReason()
    {
        Assert.Throws<ArgumentNullException>(() =>
            PublicWorldExportReadiness.Unsupported(null!));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void UnsupportedRejectsBlankReason(string reason)
    {
        Assert.Throws<ArgumentException>(() =>
            PublicWorldExportReadiness.Unsupported(reason));
    }

    [Fact]
    public void UnsupportedPreservesConcreteReason()
    {
        var readiness = PublicWorldExportReadiness.Unsupported(
            "This captured-state boundary is not qualified for public distribution.");

        Assert.False(readiness.IsSupported);
        Assert.Equal(
            "This captured-state boundary is not qualified for public distribution.",
            readiness.Reason);
    }
}
