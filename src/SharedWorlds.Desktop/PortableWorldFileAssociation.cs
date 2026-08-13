using System.IO;
using System.Runtime.InteropServices;
using System.Security;
using Microsoft.Win32;

namespace SharedWorlds.Desktop;

/// <summary>
/// Registers SafeWorld as an available per-user handler for .safeworld without attempting to
/// take over the user's Windows default-app choice. Windows remains authoritative over UserChoice.
/// </summary>
internal static class PortableWorldFileAssociation
{
    internal const string Extension = ".safeworld";
    internal const string ProgId = "SafeWorld.PortableWorld";
    internal const string RegisteredApplicationName = "SafeWorld";
    private const string DefaultSoftwareRoot = "Software";
    private const uint AssociationChanged = 0x08000000;
    private const uint IdList = 0x0000;

    internal static bool TryRegisterCurrentExecutable()
    {
        var executablePath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            return false;
        }

        try
        {
            Register(Registry.CurrentUser, executablePath, DefaultSoftwareRoot);
            SHChangeNotify(AssociationChanged, IdList, IntPtr.Zero, IntPtr.Zero);
            return true;
        }
        catch (Exception exception) when (
            exception is UnauthorizedAccessException or SecurityException or IOException)
        {
            // Shell integration is optional. SafeWorld must still start and retain its explicit
            // More -> Open World File path when policy or profile storage blocks registry writes.
            return false;
        }
    }

    internal static void Register(
        RegistryKey currentUser,
        string executablePath,
        string softwareRoot)
    {
        ArgumentNullException.ThrowIfNull(currentUser);
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(softwareRoot);

        var fullExecutablePath = Path.GetFullPath(executablePath);
        var classesRoot = $"{softwareRoot}\\Classes";
        var progIdPath = $"{classesRoot}\\{ProgId}";
        var capabilitiesPath = $"{softwareRoot}\\SafeWorld\\Capabilities";

        using (var progId = currentUser.CreateSubKey(progIdPath, writable: true)
                            ?? throw new IOException("Could not register the SafeWorld file type."))
        {
            progId.SetValue(null, "SafeWorld portable World", RegistryValueKind.String);
        }

        using (var icon = currentUser.CreateSubKey($"{progIdPath}\\DefaultIcon", writable: true)
                          ?? throw new IOException("Could not register the SafeWorld file icon."))
        {
            icon.SetValue(null, $"\"{fullExecutablePath}\",0", RegistryValueKind.String);
        }

        using (var command = currentUser.CreateSubKey($"{progIdPath}\\shell\\open\\command", writable: true)
                             ?? throw new IOException("Could not register the SafeWorld open command."))
        {
            command.SetValue(
                null,
                $"\"{fullExecutablePath}\" \"%1\"",
                RegistryValueKind.String);
        }

        // OpenWithProgids advertises SafeWorld as a handler but deliberately leaves the extension's
        // default value untouched. Windows/user UserChoice remains authoritative.
        using (var openWith = currentUser.CreateSubKey(
                   $"{classesRoot}\\{Extension}\\OpenWithProgids",
                   writable: true)
                              ?? throw new IOException("Could not register SafeWorld in Open With."))
        {
            openWith.SetValue(ProgId, string.Empty, RegistryValueKind.String);
        }

        using (var capabilities = currentUser.CreateSubKey(capabilitiesPath, writable: true)
                                  ?? throw new IOException("Could not register SafeWorld capabilities."))
        {
            capabilities.SetValue("ApplicationName", "SafeWorld", RegistryValueKind.String);
            capabilities.SetValue(
                "ApplicationDescription",
                "Open portable game Worlds with SafeWorld.",
                RegistryValueKind.String);
            capabilities.SetValue(
                "ApplicationIcon",
                $"\"{fullExecutablePath}\",0",
                RegistryValueKind.String);
        }

        using (var fileAssociations = currentUser.CreateSubKey(
                   $"{capabilitiesPath}\\FileAssociations",
                   writable: true)
                                      ?? throw new IOException("Could not register SafeWorld file associations."))
        {
            fileAssociations.SetValue(Extension, ProgId, RegistryValueKind.String);
        }

        using var registeredApplications = currentUser.CreateSubKey(
            $"{softwareRoot}\\RegisteredApplications",
            writable: true)
                                          ?? throw new IOException("Could not register SafeWorld with Default Apps.");
        registeredApplications.SetValue(
            RegisteredApplicationName,
            capabilitiesPath,
            RegistryValueKind.String);
    }

    [DllImport("shell32.dll")]
    private static extern void SHChangeNotify(
        uint wEventId,
        uint uFlags,
        IntPtr dwItem1,
        IntPtr dwItem2);
}
