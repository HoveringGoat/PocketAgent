using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using PocketAgent;
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
            CheckHeaderNavigation(window);
            CheckCodexSwitchPolicy();
            Pump(Check().WaitAsync(TimeSpan.FromSeconds(35)));
        }
        finally
        {
            window.Close();
            if (startupValue is not null) startup.SetValue("PocketAgent", startupValue, startupKind);
        }
        Console.WriteLine("PASS update feedback, startup prompts and deferral, header-only navigation, and the idle Codex switch at both window sizes.");

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
            settings.CheckUpdatesOnStartup = true;
            window.Width = 800;
            window.Height = 580;
            await CheckStartupOffer(window, 100, "Later");
            window.Width = 1150;
            window.Height = 820;
            await CheckStartupOffer(window, 101, "Install update");
            settings.AutomaticUpdates = true;
            await CheckStartupOffer(window, 102, null);
            settings.AutomaticUpdates = false;
            await CheckCodexRestart(window);
        }

        T Find<T>(string name) where T : FrameworkElement =>
            window.FindName(name) as T ?? throw new InvalidOperationException($"The update status UI is missing {name}.");
        Task Invoke(string name, params object[] arguments) =>
            (Task)typeof(MainWindow).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, arguments)!;
    }

    private static async Task CheckStartupOffer(MainWindow window, int version, string? choice, bool alreadyQueued = false)
    {
        var release = StartupRelease(version);
        var promptCount = 0;
        var installerCount = 0;
        Exception? dialogError = null;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(20) };
        timer.Tick += (_, _) =>
        {
            var dialog = window.OwnedWindows.Cast<Window>().FirstOrDefault(owned => owned.IsVisible);
            if (dialog is null) return;
            try
            {
                if (dialog.Title == "Updating PocketAgent")
                {
                    var close = Descendants(dialog).OfType<Button>().FirstOrDefault(button => Equals(button.Content, "Close"));
                    if (close is null) return;
                    installerCount++;
                    dialog.Close();
                    return;
                }
                promptCount++;
                if (choice is null) throw new InvalidOperationException("An automatic update displayed a confirmation.");
                var viewport = (ScrollViewer)dialog.Content;
                var buttons = Descendants(viewport).OfType<Button>().ToArray();
                if (buttons.Select(button => button.Content?.ToString()).SequenceEqual(new[] { "Install update", "Later" }) == false ||
                    !Descendants(viewport).OfType<TextBlock>().Any(label => label.Text.Contains(release.Version.ToString())))
                    throw new InvalidOperationException("The update prompt must show its version and both choices.");
                foreach (var button in buttons)
                {
                    var bounds = button.TransformToAncestor(viewport).TransformBounds(new Rect(button.RenderSize));
                    if (!button.IsVisible || bounds.IsEmpty || !new Rect(viewport.RenderSize).Contains(bounds))
                        throw new InvalidOperationException($"Update prompt button {button.Content} is outside the dialog.");
                }
                buttons.Single(button => Equals(button.Content, choice)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            }
            catch (Exception exception) { dialogError = exception; dialog.Close(); }
        };
        timer.Start();
        try
        {
            if (alreadyQueued)
                await (Task)typeof(MainWindow).GetMethod("OfferQueuedUpdate", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, null)!;
            else
                await InvokeStartupCheck(window, release);
            await InvokeStartupCheck(window, release);
            if (dialogError is not null) throw dialogError;
            if (promptCount != (choice is null ? 0 : 1) || installerCount != (choice == "Later" ? 0 : 1))
                throw new InvalidOperationException("Startup must offer the update once and only start installation when accepted or automatic.");
            if (choice == "Later" && ((Button)window.FindName("UpdateNotesButton")).Visibility != Visibility.Visible)
                throw new InvalidOperationException("Later must keep the update available in Settings.");
        }
        finally { timer.Stop(); }
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }

    private static DesktopUpdate StartupRelease(int major)
    {
        var url = new Uri("https://example.invalid/fixture");
        return new(new Version(major, 0, 0), "fixture", url, url.ToString(), url, "");
    }

    private static Task InvokeStartupCheck(MainWindow window, DesktopUpdate release) =>
        (Task)typeof(MainWindow).GetMethod("CheckForUpdatesCore", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(window, [true, false, (Func<Task<DesktopUpdate?>>)(() => Task.FromResult<DesktopUpdate?>(release))])!;

    private static void CheckHeaderNavigation(MainWindow window)
    {
        var navigation = (TabControl)window.FindName("Navigation");
        foreach (var size in new[] { new Size(800, 580), new Size(1150, 820) })
        {
            window.Width = size.Width;
            window.Height = size.Height;
            foreach (var (name, index) in new[] { ("SessionsNavButton", 0), ("DevicesNavButton", 1), ("SettingsNavButton", 2) })
            {
                var button = (Button)window.FindName(name);
                button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                window.UpdateLayout();
                if (navigation.SelectedIndex != index || !button.IsVisible ||
                    !new Rect(window.RenderSize).Contains(button.TransformToAncestor(window).TransformBounds(new Rect(button.RenderSize))))
                    throw new InvalidOperationException($"Header navigation {name} is unreachable at {size}.");
                if (navigation.Items.OfType<TabItem>().Any(item => item.IsVisible))
                    throw new InvalidOperationException("The page selector is rendering a second navigation row.");
            }
        }
    }

    private static void CheckCodexSwitchPolicy()
    {
        var predicate = typeof(MainWindow).GetMethod("HasNewerCodexExecutable", BindingFlags.NonPublic | BindingFlags.Static)!;
        CodexExecutable Extension(string path, int major) =>
            new(path, $"VS Code extension {major}.0.0", new Version(major, 0, 0));
        var old = Extension("old.exe", 1);
        var newer = Extension("new.exe", 2);
        bool Available(HostSettings host, CodexExecutable? running, CodexExecutable candidate) =>
            (bool)predicate.Invoke(null, [host, running, candidate])!;
        if (!Available(new(), old, newer) || Available(new(), newer, old) || Available(new(), newer, newer) ||
            Available(new(), null, newer) || Available(new() { CodexExecutable = "override.exe" }, old, newer))
            throw new InvalidOperationException("Codex restart requires a running older executable and must honor explicit overrides.");
    }

    private static async Task CheckCodexRestart(MainWindow window)
    {
        var node = Environment.GetEnvironmentVariable("POCKETAGENT_TEST_NODE") ??
            (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator)
                .Select(directory => Path.Combine(directory, "node.exe")).FirstOrDefault(File.Exists) ??
            throw new InvalidOperationException("Node is required for the Codex restart fixture.");
        var fixture = Path.Combine(DesktopSettings.DirectoryPath, "codex-restart");
        var extensions = Path.Combine(fixture, "extensions");
        var architecture = System.Runtime.InteropServices.RuntimeInformation.OSArchitecture == System.Runtime.InteropServices.Architecture.Arm64
            ? "windows-aarch64" : "windows-x86_64";
        void Install(string version)
        {
            var binary = Path.Combine(extensions, $"openai.chatgpt-{version}", "bin", architecture);
            Directory.CreateDirectory(binary);
            File.Copy(node, Path.Combine(binary, "codex.exe"));
        }
        Install("1.0.0");
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        var host = new HostSettings
        {
            RepositoryPath = fixture,
            DataDirectory = fixture,
            CodexExtensionsDirectory = extensions,
            Port = port,
            EnableHeartbeats = false,
            CodexArguments = [Path.GetFullPath("tests/fixtures/codex.mjs")]
        };
        Task Invoke(string name, params object[] arguments) =>
            (Task)typeof(MainWindow).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, arguments)!;
        var relay = typeof(MainWindow).GetField("relay", BindingFlags.NonPublic | BindingFlags.Instance)!;
        ((DispatcherTimer)typeof(MainWindow).GetField("timer", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window)!).Stop();
        try
        {
            await Invoke("StartServer", host);
            var first = (WebApplication)relay.GetValue(window)!;
            var initial = first.Services.GetRequiredService<CodexConnection>();
            if (!initial.IsReady || initial.Executable?.ExtensionVersion != new Version(1, 0, 0))
                throw new InvalidOperationException("The old Codex fixture did not start.");
            var conversations = first.Services.GetRequiredService<Conversations>();
            var conversation = await conversations.Create("Codex restart fixture");
            await conversations.StartTurn(conversation.Id, "hold");
            await InvokeStartupCheck(window, StartupRelease(103));
            if (window.OwnedWindows.Cast<Window>().Any(dialog => dialog.IsVisible) ||
                !((TextBlock)window.FindName("UpdateStatus")).Text.Contains("waiting for conversations"))
                throw new InvalidOperationException("A startup update must stay queued while a conversation is active.");
            Install("2.0.0");
            var navigation = (TabControl)window.FindName("Navigation");
            navigation.SelectedIndex = 0;
            navigation.SelectedIndex = 2;
            await Invoke("Refresh");
            var button = (Button)window.FindName("CodexRestartButton");
            var status = (TextBlock)window.FindName("CodexVersionStatus");
            if (button.Visibility != Visibility.Visible || button.IsEnabled || !status.Text.Contains("1.0.0") || !status.Text.Contains("2.0.0"))
                throw new InvalidOperationException("An available Codex switch must show both versions and wait for the active turn.");
            foreach (var size in new[] { new Size(800, 580), new Size(1150, 820) }) CheckLayout(window, size, status, button);
            await Invoke("SwitchToUpdatedCodex");
            if (relay.GetValue(window) != first) throw new InvalidOperationException("Restart interrupted the active turn.");
            await conversations.Interrupt(conversation.Id);
            for (var i = 0; i < 50 && (await conversations.List()).Any(entry => Conversations.IsActiveStatus(entry.Status)); i++) await Task.Delay(20);
            await Invoke("Refresh");
            await CheckStartupOffer(window, 103, "Later", alreadyQueued: true);
            if (!button.IsEnabled) throw new InvalidOperationException("The Codex restart must be enabled after the turn ends.");
            await Invoke("SwitchToUpdatedCodex");
            var current = (WebApplication)relay.GetValue(window)!;
            var updated = current.Services.GetRequiredService<CodexConnection>();
            if (current == first || !updated.IsReady || updated.Executable?.ExtensionVersion != new Version(2, 0, 0) ||
                button.Visibility != Visibility.Collapsed || !status.Text.Contains("2.0.0"))
                throw new InvalidOperationException("The restarted server must confirm the new executable and clear the restart action.");
            var restartedConversations = current.Services.GetRequiredService<Conversations>();
            if (!(await restartedConversations.List()).Any(entry => entry.Id == conversation.Id))
                throw new InvalidOperationException("The Codex restart lost the saved conversation.");
            if (!await restartedConversations.TryPrepareIdleRestart())
                throw new InvalidOperationException("An idle server should reserve the restart.");
            try
            {
                await restartedConversations.StartTurn(conversation.Id, "A turn submitted after reserving the restart");
                throw new InvalidOperationException("A new turn entered after the restart was reserved.");
            }
            catch (ApiException exception) when (exception.Status == 409 && exception.Message.Contains("restarting")) { }
        }
        finally { await Invoke("Shutdown"); }
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
