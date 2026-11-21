using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Threading;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        application.Dispatcher.InvokeAsync(async () => application.Shutdown(await RunUpdate(args)));
        return application.Run();
    }

    private static async Task<int> RunUpdate(string[] args)
    {
        var arguments = Parse(args);
        var logFile = arguments.GetValueOrDefault("log-file", Path.Combine(Path.GetTempPath(), "PocketAgent-update.log"));
        foreach (var required in new[] { "asset-url", "install-root", "restart-path", "wait-pid" })
            if (!arguments.ContainsKey(required)) return Fail($"Missing updater argument: {required}.", logFile);

        var installRoot = Path.GetFullPath(arguments["install-root"]);
        var restartPath = Path.GetFullPath(arguments["restart-path"]);
        var installParent = Directory.GetParent(installRoot)?.FullName ?? throw new InvalidOperationException("The install root must have a parent directory.");
        var staging = Path.Combine(installParent, ".PocketAgent-update-" + Guid.NewGuid().ToString("N"));
        var parentExited = false;
        var healthFile = arguments.GetValueOrDefault("health-file");
        var pipeName = arguments.GetValueOrDefault("progress-pipe");
        using var pipe = pipeName is null ? null : new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        StreamWriter? progress = null;
        UpdaterWindow? window = null;
        try
        {
            if (pipe is not null)
            {
                await pipe.ConnectAsync(15000);
                progress = new StreamWriter(pipe) { AutoFlush = true };
            }
            WriteLog(logFile, $"Starting update to {arguments.GetValueOrDefault("version", "unknown")}.");
            Directory.CreateDirectory(staging);
            using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
            client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("PocketAgent.Updater", "0.2.0"));
            var installer = Path.Combine(staging, "PocketAgent-Setup.exe");
            using var downloadTimeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
            await Download(client, arguments["asset-url"], installer, progress, downloadTimeout.Token);
            if (progress is not null) await progress.WriteLineAsync("verifying");
            if (arguments.TryGetValue("sha256-url", out var checksumUrl))
            {
                var checksumText = await client.GetStringAsync(checksumUrl);
                var expected = checksumText.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
                await using var installerStream = File.OpenRead(installer);
                var actual = Convert.ToHexString(await SHA256.HashDataAsync(installerStream)).ToLowerInvariant();
                if (string.IsNullOrWhiteSpace(expected) || !CryptographicOperations.FixedTimeEquals(Convert.FromHexString(expected), Convert.FromHexString(actual)))
                    throw new InvalidOperationException("The downloaded desktop installer failed checksum verification.");
            }
            WriteLog(logFile, "Package downloaded and checksum verified.");
            window = new UpdaterWindow(arguments.GetValueOrDefault("version", ""));
            window.Show();
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            if (progress is not null)
            {
                await progress.WriteLineAsync("ready");
                using var reader = new StreamReader(pipe!, leaveOpen: true);
                using var decisionTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                if (await reader.ReadLineAsync(decisionTimeout.Token) != "install")
                {
                    WriteLog(logFile, "Desktop declined the installer handoff.");
                    window.Finish();
                    return 0;
                }
            }
            await WaitForExit(int.Parse(arguments["wait-pid"], CultureInfo.InvariantCulture)).WaitAsync(TimeSpan.FromMinutes(2));
            parentExited = true;
            WriteLog(logFile, "Parent application exited.");
            window.SetStatus("Installing the update…");
            var installerInfo = new ProcessStartInfo(installer)
            {
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                WorkingDirectory = staging
            };
            installerInfo.ArgumentList.Add("/VERYSILENT");
            installerInfo.ArgumentList.Add("/SUPPRESSMSGBOXES");
            installerInfo.ArgumentList.Add("/NORESTART");
            installerInfo.ArgumentList.Add("/DIR=" + installRoot);
            using var installerProcess = Process.Start(installerInfo) ?? throw new InvalidOperationException("The desktop installer could not be started.");
            await installerProcess.WaitForExitAsync();
            if (installerProcess.ExitCode != 0) throw new InvalidOperationException($"The desktop installer exited with code {installerProcess.ExitCode}.");
            WriteLog(logFile, "Desktop installer completed.");
            window.SetStatus("Starting PocketAgent…");
            var restart = new ProcessStartInfo(restartPath)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(restartPath) ?? installRoot
            };
            if (!string.IsNullOrWhiteSpace(healthFile)) { restart.ArgumentList.Add("--health-file"); restart.ArgumentList.Add(healthFile); }
            restart.ArgumentList.Add("--update-version"); restart.ArgumentList.Add(arguments.GetValueOrDefault("version", "unknown"));
            using var process = Process.Start(restart);
            if (process is null) throw new InvalidOperationException("The updated desktop could not be started.");
            WriteLog(logFile, "Updated application started; waiting for health check.");
            var healthy = await WaitForFile(arguments.GetValueOrDefault("health-file"), TimeSpan.FromSeconds(30));
            if (!healthy)
            {
                try { if (!process.HasExited) process.Kill(true); } catch { }
                window.SetStatus("PocketAgent did not confirm startup.");
                Fail("The installed update failed its startup check.", logFile);
                await window.ShowFailure();
                return 1;
            }
            WriteLog(logFile, "Update completed successfully.");
            window.Finish();
            return 0;
        }
        catch (Exception exception)
        {
            Fail($"Installer update failed: {exception}", logFile);
            if (parentExited) TryStart(restartPath);
            if (window is not null)
            {
                window.SetStatus(parentExited ? "The update could not finish. See the updater log for details." : "PocketAgent could not hand off to the installer.");
                await window.ShowFailure();
            }
            else if (progress is not null)
            {
                try { await progress.WriteLineAsync("failed"); } catch (IOException) { }
            }
            return 1;
        }
        finally
        {
            try { if (Directory.Exists(staging)) Directory.Delete(staging, true); } catch { }
            try { if (!string.IsNullOrWhiteSpace(healthFile)) File.Delete(healthFile); } catch { }
        }
    }

    static async Task Download(HttpClient client, string url, string destination, StreamWriter? progress, CancellationToken cancellationToken)
    {
        using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        var length = response.Content.Headers.ContentLength;
        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var output = File.Create(destination);
        var buffer = new byte[128 * 1024];
        long received = 0;
        var lastReport = Stopwatch.StartNew();
        while (await input.ReadAsync(buffer, cancellationToken) is var count && count > 0)
        {
            await output.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
            received += count;
            if (progress is null || lastReport.ElapsedMilliseconds < 100) continue;
            var percent = length is > 0 ? Math.Clamp((int)(100.0 * received / length.Value), 0, 100) : -1;
            await progress.WriteLineAsync(percent.ToString(CultureInfo.InvariantCulture).AsMemory(), cancellationToken);
            lastReport.Restart();
        }
    }

    static Dictionary<string, string> Parse(string[] args)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i + 1 < args.Length; i += 2) values[args[i].TrimStart('-')] = args[i + 1];
        return values;
    }

    static async Task WaitForExit(int processId)
    {
        try { using var process = Process.GetProcessById(processId); await process.WaitForExitAsync(); }
        catch (ArgumentException) { }
    }

    static async Task<bool> WaitForFile(string? path, TimeSpan timeout)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (File.Exists(path)) return true;
            await Task.Delay(250);
        }
        return false;
    }

    static void TryStart(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Path.GetDirectoryName(path) ?? AppContext.BaseDirectory });
        }
        catch { }
    }

    static int Fail(string message, string? logFile = null)
    {
        Console.Error.WriteLine(message);
        WriteLog(logFile, message);
        return 1;
    }

    static void WriteLog(string? logFile, string message)
    {
        if (string.IsNullOrWhiteSpace(logFile)) return;
        try { File.AppendAllText(logFile, $"{DateTimeOffset.Now:O} {message}{Environment.NewLine}"); } catch { }
    }
}
