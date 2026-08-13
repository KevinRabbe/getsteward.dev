using System.Globalization;
using System.Reflection;
using Xunit;

namespace SharedWorlds.Desktop.AcceptanceTests;

public sealed class DesktopTextAcceptanceTests
{
    [Fact]
    public void NeutralActionResourcesResolveThroughDesktopText()
    {
        WithUiCulture("en-US", () =>
        {
            var properties = typeof(DesktopText)
                .GetProperties(BindingFlags.Public | BindingFlags.Static)
                .Where(property => property.PropertyType == typeof(string))
                .ToArray();

            Assert.NotEmpty(properties);
            foreach (var property in properties)
            {
                var value = Assert.IsType<string>(property.GetValue(null));
                Assert.False(
                    string.IsNullOrWhiteSpace(value),
                    $"Desktop resource '{property.Name}' resolved to an empty value.");
                Assert.DoesNotContain("Safe World", value, StringComparison.Ordinal);
            }

            Assert.Equal("Add World", DesktopText.Import);
            Assert.Equal("More", DesktopText.More);
            Assert.Equal("Open World File…", DesktopText.OpenWorldFile);
            Assert.Equal("Started from {0}", DesktopText.StartedFromFormat);
            Assert.Equal("Started from {0} by {1}", DesktopText.StartedFromCreatorFormat);
            Assert.Equal("Join", DesktopText.Join);
            Assert.Equal("Stop and Save", DesktopText.StopAndSave);
            Assert.Equal("Retry recovery", DesktopText.RetryRecovery);
            Assert.Equal("Continue from last safe state", DesktopText.ContinueFromLastSafeState);
            Assert.Equal("Quit SafeWorld", DesktopText.QuitSteward);
        });
    }

    [Theory]
    [InlineData("de-DE")]
    [InlineData("fr-FR")]
    public void MissingLocalizedCatalogFallsBackToNeutralEnglish(string cultureName)
    {
        WithUiCulture(cultureName, () =>
        {
            Assert.Equal("Add World", DesktopText.Import);
            Assert.Equal("Open World File…", DesktopText.OpenWorldFile);
            Assert.Equal("Retry recovery", DesktopText.RetryRecovery);
            Assert.Equal("Continue from last safe state", DesktopText.ContinueFromLastSafeState);
        });
    }

    private static void WithUiCulture(string cultureName, Action assertion)
    {
        var previousCulture = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(cultureName);
            assertion();
        }
        finally
        {
            CultureInfo.CurrentUICulture = previousCulture;
        }
    }
}
