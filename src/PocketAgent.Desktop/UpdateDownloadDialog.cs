using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Windows;
using System.Windows.Controls;

namespace PocketAgent.Desktop;

internal static class UpdateDownloadDialog
{
    public static bool Show(Window owner, DesktopUpdate update, Func<Task<bool>> canInstall, Func<string, IDisposable?>? launch = null)
    {
        var pipeName = "PocketAgent-update-" + Guid.NewGuid().ToString("N");
        using var pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        var status = new TextBlock { Text = $"Downloading PocketAgent {update.Version}…", TextWrapping = TextWrapping.Wrap };
        var progress = new ProgressBar { Height = 8, IsIndeterminate = true, Margin = new Thickness(0, 14, 0, 0) };
        var content = new StackPanel { Margin = new Thickness(22), Children = { status, progress } };
        var dialog = new Window
        {
            Title = "Updating PocketAgent",
            Owner = owner,
            Width = 390,
            SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = content
        };
        var canClose = false;
        dialog.Closing += (_, e) => e.Cancel = !canClose;
        dialog.ContentRendered += async (_, _) =>
        {
            try
            {
                using var updater = launch is null ? UpdateService.Launch(update, Environment.ProcessId, pipeName) : launch(pipeName);
                if (updater is null)
                {
                    status.Text = "The updater could not start. Check that you are running the installed desktop release.";
                }
                else
                {
                    using (var connectTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(20)))
                        await pipe.WaitForConnectionAsync(connectTimeout.Token);
                    using var reader = new StreamReader(pipe);
                    using var writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true };
                    while (await reader.ReadLineAsync() is { } message)
                    {
                        if (message == "ready")
                        {
                            var approved = await canInstall();
                            await writer.WriteLineAsync(approved ? "install" : "defer");
                            canClose = true;
                            dialog.DialogResult = approved;
                            return;
                        }
                        if (message == "failed") break;
                        if (message == "verifying")
                        {
                            status.Text = "Verifying the installer checksum…";
                            progress.IsIndeterminate = true;
                        }
                        else if (int.TryParse(message, CultureInfo.InvariantCulture, out var percent) && percent is >= 0 and <= 100)
                        {
                            status.Text = $"Downloading PocketAgent {update.Version}… {percent}%";
                            progress.IsIndeterminate = false;
                            progress.Value = percent;
                        }
                    }
                    status.Text = "The update could not be prepared. See PocketAgent-update.log in your temporary folder for details.";
                }
            }
            catch (Exception exception)
            {
                DesktopDiagnostics.LogException("update-progress", exception);
                status.Text = "The connection to the updater ended before it was ready. Try the update again.";
            }
            progress.Visibility = Visibility.Collapsed;
            canClose = true;
            var close = new Button { Content = "Close", IsDefault = true, IsCancel = true, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
            content.Children.Add(close);
            close.Focus();
        };
        return dialog.ShowDialog() == true;
    }
}
