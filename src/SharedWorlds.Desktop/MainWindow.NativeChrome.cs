using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace SharedWorlds.Desktop;

public partial class MainWindow
{
    private const int DwmUseImmersiveDarkMode = 20;
    private const int DwmUseImmersiveDarkModeLegacy = 19;

    internal void InitializeProfessionalWindowChrome()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        SourceInitialized += (_, _) => TryEnableDarkTitleBar();
    }

    private void TryEnableDarkTitleBar()
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        var enabled = 1;
        try
        {
            var result = DwmSetWindowAttribute(
                handle,
                DwmUseImmersiveDarkMode,
                ref enabled,
                sizeof(int));
            if (result != 0)
            {
                _ = DwmSetWindowAttribute(
                    handle,
                    DwmUseImmersiveDarkModeLegacy,
                    ref enabled,
                    sizeof(int));
            }
        }
        catch (DllNotFoundException)
        {
            // Older/unsupported Windows environments keep their native title bar.
        }
        catch (EntryPointNotFoundException)
        {
            // Same fallback: appearance must never block the desktop from starting.
        }
    }

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(
        IntPtr hwnd,
        int attribute,
        ref int value,
        int valueSize);
}
