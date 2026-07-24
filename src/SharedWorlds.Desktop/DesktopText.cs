using System.Globalization;
using System.Resources;

namespace SharedWorlds.Desktop;

public static class DesktopText
{
    private static readonly ResourceManager Resources = new(
        "SharedWorlds.Desktop.DesktopText",
        typeof(DesktopText).Assembly);

    public static string Import => Get(nameof(Import));
    public static string Refresh => Get(nameof(Refresh));
    public static string BackToWorlds => Get(nameof(BackToWorlds));
    public static string StartWorld => Get(nameof(StartWorld));
    public static string HostWorld => Get(nameof(HostWorld));
    public static string Join => Get(nameof(Join));
    public static string ShareWorld => Get(nameof(ShareWorld));
    public static string ManageAccess => Get(nameof(ManageAccess));
    public static string StopAndSave => Get(nameof(StopAndSave));
    public static string RetryConnection => Get(nameof(RetryConnection));
    public static string RetrySharing => Get(nameof(RetrySharing));
    public static string RetryRecovery => Get(nameof(RetryRecovery));
    public static string ExportRecoveryCopy => Get(nameof(ExportRecoveryCopy));
    public static string ContinueFromLastSafeState => Get(nameof(ContinueFromLastSafeState));
    public static string RecoverChanges => Get(nameof(RecoverChanges));
    public static string DiscardInterruptedSession => Get(nameof(DiscardInterruptedSession));
    public static string RetryCleanup => Get(nameof(RetryCleanup));
    public static string VerifyEnvironment => Get(nameof(VerifyEnvironment));
    public static string Repair => Get(nameof(Repair));
    public static string OpenSteward => Get(nameof(OpenSteward));
    public static string QuitSteward => Get(nameof(QuitSteward));

    private static string Get(string key)
        => Resources.GetString(key, CultureInfo.CurrentUICulture)
           ?? throw new MissingManifestResourceException(
               $"Desktop string resource '{key}' is missing.");
}
