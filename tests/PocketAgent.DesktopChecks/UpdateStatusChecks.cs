using System.Net.Http;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using PocketAgent.Desktop;

internal static class UpdateStatusChecks
{
    public static void Run()
    {
        var window = new MainWindow();
        var settings = (DesktopSettings)typeof(MainWindow).GetField("settings", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window)!;
        settings.StartServerOnLaunch = false;
        settings.CheckUpdatesOnStartup = false;
        ((TabControl)window.FindName("Navigation")).SelectedIndex = 2;
        using var startup = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
        var startupValue = startup.GetValue("PocketAgent", null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        var startupKind = startupValue is null ? RegistryValueKind.Unknown : startup.GetValueKind("PocketAgent");
        try
        {
            window.Show();
            ((ComboBox)window.FindName("UiScaleSetting")).SelectedValue = "large";
            Pump(Check().WaitAsync(TimeSpan.FromSeconds(10)));
        }
        finally
        {
            window.Close();
            if (startupValue is not null) startup.SetValue("PocketAgent", startupValue, startupKind);
        }
        Console.WriteLine("PASS update checks show immediate feedback, install progress, final status, and fit at minimum and larger sizes.");

        async Task Check()
        {
            var codexStatus = (TextBlock)window.FindName("CodexUpdateStatus");
            var codexButton = Find<Button>("CodexUpdateCheckButton");
            var codexProgress = Find<ProgressBar>("CodexUpdateProgress");
            var desktopStatus = (TextBlock)window.FindName("UpdateStatus");
            var desktopButton = Find<Button>("DesktopUpdateCheckButton");
            var desktopProgress = Find<ProgressBar>("DesktopUpdateProgress");
            var finishCodex = new TaskCompletionSource<CodexExtensionUpdateResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            IProgress<Version>? progress = null;
            var codexCalls = 0;
            Func<IProgress<Version>, Task<CodexExtensionUpdateResult>> checkCodex = stage =>
            {
                codexCalls++;
                progress = stage;
                return finishCodex.Task;
            };
            var codex = Invoke("CheckCodexExtensionUpdateCore", checkCodex);
            Busy(codexStatus, codexButton, codexProgress, "Checking");
            await Invoke("CheckCodexExtensionUpdateCore", checkCodex);
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            if (codexCalls != 1) throw new InvalidOperationException("A second Codex check started while the first was pending.");
            progress!.Report(new Version(int.MaxValue, int.MaxValue, int.MaxValue));
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Busy(codexStatus, codexButton, codexProgress, "Installing Codex");
            foreach (var size in new[] { new Size(800, 580), new Size(1150, 820) })
            {
                CheckLayout(window, size, codexStatus, codexProgress, codexButton);
            }
            finishCodex.SetResult(new(true, new Version(1, 0), new Version(2, 0), "Installed. Restart the PocketAgent server."));
            await codex;
            Idle(codexStatus, codexButton, codexProgress, "Restart");

            var finishDesktop = new TaskCompletionSource<DesktopUpdate?>(TaskCreationOptions.RunContinuationsAsynchronously);
            Func<Task<DesktopUpdate?>> checkDesktop = () => finishDesktop.Task;
            var desktop = Invoke("CheckForUpdatesCore", false, true, checkDesktop);
            Busy(desktopStatus, desktopButton, desktopProgress, "Checking");
            foreach (var size in new[] { new Size(800, 580), new Size(1150, 820) })
                CheckLayout(window, size, desktopStatus, desktopProgress, desktopButton);
            finishDesktop.SetException(new HttpRequestException("Release service unavailable"));
            await desktop;
            Idle(desktopStatus, desktopButton, desktopProgress, "Release service unavailable");
        }

        T Find<T>(string name) where T : FrameworkElement =>
            window.FindName(name) as T ?? throw new InvalidOperationException($"The update status UI is missing {name}.");
        Task Invoke(string name, params object[] arguments) =>
            (Task)typeof(MainWindow).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, arguments)!;
    }

    private static void Busy(TextBlock status, Button button, ProgressBar progress, string message)
    {
        if (!status.Text.Contains(message) || button.IsEnabled || progress.Visibility != Visibility.Visible || !progress.IsIndeterminate)
            throw new InvalidOperationException($"Expected visible '{message}' feedback and a disabled check button.");
    }

    private static void Idle(TextBlock status, Button button, ProgressBar progress, string message)
    {
        if (!status.Text.Contains(message) || !button.IsEnabled || progress.Visibility != Visibility.Collapsed)
            throw new InvalidOperationException($"Expected final '{message}' feedback and an enabled check button.");
    }

    private static void CheckLayout(Window window, Size size, params FrameworkElement[] controls)
    {
        window.Width = size.Width;
        window.Height = size.Height;
        window.UpdateLayout();
        DependencyObject? scroll = controls[0];
        while (scroll is not ScrollViewer) scroll = VisualTreeHelper.GetParent(scroll);
        var viewport = (ScrollViewer)scroll;
        var statusTop = controls[0].TranslatePoint(new Point(), viewport).Y;
        viewport.ScrollToVerticalOffset(viewport.VerticalOffset + statusTop - 16);
        window.UpdateLayout();
        var bottom = double.NegativeInfinity;
        foreach (var control in controls)
        {
            var bounds = control.TransformToAncestor(viewport).TransformBounds(new Rect(control.RenderSize));
            if (bounds.Width <= 0 || bounds.Height <= 0 || bounds.Left < 0 || bounds.Right > viewport.ActualWidth ||
                bounds.Top < 0 || bounds.Bottom > viewport.ActualHeight || bounds.Top < bottom)
                throw new InvalidOperationException($"Update control {control.Name} is clipped or overlaps at {size}.");
            bottom = bounds.Bottom;
        }
    }

    private static void Pump(Task task)
    {
        var frame = new DispatcherFrame();
        var dispatcher = Dispatcher.CurrentDispatcher;
        _ = task.ContinueWith(_ => dispatcher.InvokeAsync(() => frame.Continue = false), TaskScheduler.Default);
        Dispatcher.PushFrame(frame);
        task.GetAwaiter().GetResult();
    }
}
