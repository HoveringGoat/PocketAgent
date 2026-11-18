using System.Windows;
using System.Windows.Media;
using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace PocketAgent.Desktop;

public partial class App : Application
{
    public App()
    {
        // The desktop window must remain visible on hosts where WPF's hardware
        // compositor produces a blank client surface (RDP/VM and some display
        // driver combinations). Software rendering keeps the live window and
        // captured/remote frames consistent with the visual tree.
        RenderOptions.ProcessRenderMode = RenderMode.SoftwareOnly;
        DesktopDiagnostics.Initialize();
        DispatcherUnhandledException += (_, args) =>
        {
            DesktopDiagnostics.LogException("dispatcher", args.Exception);
            args.Handled = false;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception exception) DesktopDiagnostics.LogException("app-domain", exception);
            else DesktopDiagnostics.Log($"EXCEPTION source=app-domain object={args.ExceptionObject}");
        };
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            DesktopDiagnostics.LogException("task-scheduler", args.Exception);
            args.SetObserved();
        };
        Exit += (_, _) => DesktopDiagnostics.LogShutdown("application-exit");
    }

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
