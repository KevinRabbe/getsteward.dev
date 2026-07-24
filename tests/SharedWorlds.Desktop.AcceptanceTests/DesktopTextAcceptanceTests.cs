using System.Globalization;
using System.Reflection;
using Xunit;

namespace SharedWorlds.Desktop.AcceptanceTests;

public sealed class DesktopTextAcceptanceTests
{
    [Fact]
    public void NeutralActionResourcesResolveThroughDesktopText()
    {
        var previousCulture = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en-US");

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
            }

            Assert.Equal("Import", DesktopText.Import);
            Assert.Equal("Join", DesktopText.Join);
            Assert.Equal("Stop and Save", DesktopText.StopAndSave);
            Assert.Equal("Quit Steward", DesktopText.QuitSteward);
        }
        finally
        {
            CultureInfo.CurrentUICulture = previousCulture;
        }
    }
}
