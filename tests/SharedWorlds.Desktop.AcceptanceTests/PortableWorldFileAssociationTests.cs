using Microsoft.Win32;
using Xunit;

namespace SharedWorlds.Desktop.AcceptanceTests;

public sealed class PortableWorldFileAssociationTests
{
    [Fact]
    public void RegistersHandlerWithoutTakingOverExtensionDefault()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var testRoot = $"Software\\SafeWorldAssociationTests\\{Guid.NewGuid():N}";
        const string executablePath = @"C:\Program Files\Safe World\SharedWorlds.Desktop.exe";
        try
        {
            PortableWorldFileAssociation.Register(
                Registry.CurrentUser,
                executablePath,
                testRoot);

            using var extension = Registry.CurrentUser.OpenSubKey(
                $"{testRoot}\\Classes\\{PortableWorldFileAssociation.Extension}");
            Assert.NotNull(extension);
            Assert.Null(extension!.GetValue(null));

            using var openWith = Registry.CurrentUser.OpenSubKey(
                $"{testRoot}\\Classes\\{PortableWorldFileAssociation.Extension}\\OpenWithProgids");
            Assert.NotNull(openWith);
            Assert.Equal(
                string.Empty,
                openWith!.GetValue(PortableWorldFileAssociation.ProgId));

            using var command = Registry.CurrentUser.OpenSubKey(
                $"{testRoot}\\Classes\\{PortableWorldFileAssociation.ProgId}\\shell\\open\\command");
            Assert.NotNull(command);
            Assert.Equal(
                $"\"{executablePath}\" \"%1\"",
                command!.GetValue(null));

            using var associations = Registry.CurrentUser.OpenSubKey(
                $"{testRoot}\\SafeWorld\\Capabilities\\FileAssociations");
            Assert.NotNull(associations);
            Assert.Equal(
                PortableWorldFileAssociation.ProgId,
                associations!.GetValue(PortableWorldFileAssociation.Extension));

            using var registeredApplications = Registry.CurrentUser.OpenSubKey(
                $"{testRoot}\\RegisteredApplications");
            Assert.NotNull(registeredApplications);
            Assert.Equal(
                $"{testRoot}\\SafeWorld\\Capabilities",
                registeredApplications!.GetValue(PortableWorldFileAssociation.RegisteredApplicationName));
        }
        finally
        {
            Registry.CurrentUser.DeleteSubKeyTree(testRoot, throwOnMissingSubKey: false);
        }
    }
}
