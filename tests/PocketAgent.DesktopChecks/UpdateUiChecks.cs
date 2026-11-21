using System.IO;
using System.IO.Pipes;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using PocketAgent.Desktop;

internal static class UpdateUiChecks
{
    public static void Run()
    {
        var show = typeof(UpdateService).Assembly.GetType("PocketAgent.Desktop.UpdateDownloadDialog")!.GetMethod("Show")!;
        var url = new Uri("https://example.invalid/update");
        var update = new DesktopUpdate(new Version(int.MaxValue, int.MaxValue, int.MaxValue), "fixture", url, url.ToString(), url, "");
        var owner = new Window { Width = 800, Height = 580, ShowInTaskbar = false };
        owner.Show();
        try
        {
            Check(approve: true, fail: false);
            owner.Width = 1150;
            owner.Height = 820;
            Check(approve: false, fail: false);
            Check(approve: false, fail: true);
        }
        finally { owner.Close(); }
        Console.WriteLine("PASS desktop update modal progress, handoff, deferral, failure, and layout at 800×580 and 1150×820.");

        void Check(bool approve, bool fail)
        {
            Task? fixture = null;
            Func<string, IDisposable?> launch = name =>
            {
                var dialog = owner.OwnedWindows.Cast<Window>().Single();
                var content = (StackPanel)dialog.Content;
                var status = (TextBlock)content.Children[0];
                var progress = (ProgressBar)content.Children[1];
                fixture = DriveAndCloseOnError();
                return new MemoryStream();

                async Task DriveAndCloseOnError()
                {
                    try { await Drive(); }
                    catch
                    {
                        await Observe(() => content.Children.Count == 3 || !dialog.IsVisible);
                        if (dialog.IsVisible) dialog.Close();
                        throw;
                    }
                }

                async Task Drive()
                {
                    using var pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
                    await pipe.ConnectAsync(5000);
                    using var writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true };
                    await writer.WriteLineAsync("42");
                    await Observe(() => progress.Value == 42);
                    if (!owner.IsVisible || progress.IsIndeterminate || !status.Text.EndsWith("42%"))
                        throw new InvalidOperationException("Download progress must be visible with the owner still open.");
                    CheckBounds(dialog, status, progress);
                    await writer.WriteLineAsync("verifying");
                    await Observe(() => status.Text.Contains("checksum"));
                    CheckBounds(dialog, status, progress);
                    if (fail)
                    {
                        await writer.WriteLineAsync("failed");
                        await Observe(() => content.Children.Count == 3);
                        var close = (Button)content.Children[2];
                        CheckBounds(dialog, status, close);
                        dialog.Close();
                    }
                    else
                    {
                        using var reader = new StreamReader(pipe, leaveOpen: true);
                        await writer.WriteLineAsync("ready");
                        // Read on the pipe independently of the modal dispatcher closing.
                        if (await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false) != (approve ? "install" : "defer"))
                            throw new InvalidOperationException("The desktop sent the wrong installer decision.");
                    }
                }
            };
            var result = (bool)show.Invoke(null, [owner, update, (Func<Task<bool>>)(() => Task.FromResult(approve)), launch])!;
            var frame = new DispatcherFrame();
            _ = fixture!.ContinueWith(_ => owner.Dispatcher.InvokeAsync(() => frame.Continue = false), TaskScheduler.Default);
            Dispatcher.PushFrame(frame);
            fixture.GetAwaiter().GetResult();
            if (result != approve) throw new InvalidOperationException("The download modal returned the wrong installer decision.");
        }
    }

    private static async Task Observe(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 100 && !condition(); attempt++) await Task.Delay(20);
        if (!condition()) throw new InvalidOperationException("The expected update state was not rendered.");
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
    }

    private static void CheckBounds(Window window, params FrameworkElement[] elements)
    {
        window.UpdateLayout();
        var content = (FrameworkElement)window.Content;
        var bounds = new Rect(content.RenderSize);
        var previousBottom = 0.0;
        foreach (var element in elements)
        {
            var rectangle = element.TransformToAncestor(content).TransformBounds(new Rect(element.RenderSize));
            if (rectangle.Top < previousBottom || rectangle.Width <= 0 || rectangle.Height <= 0 || !bounds.Contains(rectangle))
                throw new InvalidOperationException("An update dialog control was clipped or overlapped.");
            previousBottom = rectangle.Bottom;
        }
    }
}
