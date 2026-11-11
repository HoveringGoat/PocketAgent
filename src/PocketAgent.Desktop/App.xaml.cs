using System.Windows;
using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace PocketAgent.Desktop;

public partial class App : Application
{
    private void WindowLoaded(object sender, RoutedEventArgs args)
    {
        if (sender is not Window window || !ReferenceEquals(args.OriginalSource, window)) return;
        var handle = new WindowInteropHelper(window).Handle;
        var dark = 1;
        _ = DwmSetWindowAttribute(handle, 20, ref dark, sizeof(int));
        var background = 0x00171B11; // COLORREF: dark green #111B17.
        _ = DwmSetWindowAttribute(handle, 35, ref background, sizeof(int));
        var foreground = 0x00A6DAC1; // COLORREF: light green #C1DAA6.
        _ = DwmSetWindowAttribute(handle, 36, ref foreground, sizeof(int));
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);
}
