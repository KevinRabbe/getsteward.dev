using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace SharedWorlds.Desktop;

public partial class MainWindow
{
    private const int DwmUseImmersiveDarkMode = 20;
    private const int DwmUseImmersiveDarkModeLegacy = 19;

    internal void InitializeProfessionalWindowChrome()
    {
        Icon = CreateSafeWorldWindowIcon();

        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        SourceInitialized += (_, _) => TryEnableDarkTitleBar();
    }

    private static ImageSource CreateSafeWorldWindowIcon()
    {
        var gradient = new LinearGradientBrush(
            Color.FromRgb(0x78, 0x93, 0xFF),
            Color.FromRgb(0x9B, 0x7C, 0xFF),
            new Point(0, 0),
            new Point(1, 1));
        var white = new SolidColorBrush(Color.FromRgb(0xF8, 0xFA, 0xFF));
        var outline = new Pen(white, 3.2)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
            LineJoin = PenLineJoin.Round
        };
        var detail = new Pen(white, 2.4)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round
        };

        var group = new DrawingGroup();
        group.Children.Add(new GeometryDrawing(
            gradient,
            null,
            new RectangleGeometry(new Rect(2, 2, 60, 60), 14, 14)));
        group.Children.Add(new GeometryDrawing(
            null,
            outline,
            new EllipseGeometry(new Point(32, 32), 18, 18)));
        group.Children.Add(new GeometryDrawing(
            null,
            detail,
            new EllipseGeometry(new Point(32, 32), 8, 18)));
        group.Children.Add(new GeometryDrawing(
            null,
            detail,
            new EllipseGeometry(new Point(32, 32), 18, 8)));

        group.Freeze();
        var image = new DrawingImage(group);
        image.Freeze();
        return image;
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
