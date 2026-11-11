using System.Diagnostics;
using System.IO;
using System.Net;
using System.Text.RegularExpressions;

namespace PocketAgent.Desktop;

public sealed class InternetConnector : IDisposable
{
    private Process? process;
    private HostSettings? temporarySettings;
    public event Action<string>? AddressChanged;
    public bool IsRunning => process is { HasExited: false };
    public void Start(DesktopSettings settings)
    {
        if (IsRunning) return;
        var executable = Executable(settings.TunnelExecutable);
        if (!File.Exists(executable) || !File.Exists(settings.TunnelConfiguration))
            throw new InvalidOperationException("Select cloudflared and your named tunnel configuration in Settings. See the included setup guide.");
        if (settings.Host.PublicOrigin.Length == 0)
            throw new InvalidOperationException("Configure a public HTTPS address first.");
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in new[] { "tunnel", "--config", Path.GetFullPath(settings.TunnelConfiguration), "run" })
            start.ArgumentList.Add(argument);
        process = Process.Start(start) ?? throw new InvalidOperationException("Could not start the internet connector.");
        // Connector output can include configuration and service details; do not relay it to logs or phones.
        _ = Drain(process.StandardOutput);
        _ = Drain(process.StandardError);
    }
    private static string Executable(string configured) => configured.Length > 0 ? configured : Path.Combine(AppContext.BaseDirectory, "tools", "cloudflared.exe");
    public void StartTemporary(DesktopSettings settings)
    {
        if (IsRunning) throw new InvalidOperationException("Stop the current connector first.");
        if (!IPAddress.IsLoopback(IPAddress.Parse(settings.Host.ListenAddress)) || settings.Host.UseHttps || settings.Host.PublicOrigin.Length > 0)
            throw new InvalidOperationException("Temporary internet access needs a loopback HTTP listener and no public address. Stop the server, select Prepare internet listener, then start the server again.");
        var executable = Executable(settings.TunnelExecutable);
        if (!File.Exists(executable)) throw new InvalidOperationException("Use the packaged cloudflared connector or select its executable.");
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        var quickConfiguration = Path.Combine(settings.Host.DataDirectory, "quick-tunnel.yml");
        File.WriteAllText(quickConfiguration, "{}\n");
        foreach (var argument in new[] { "tunnel", "--no-autoupdate", "--config", quickConfiguration, "--url", settings.Host.Origin }) start.ArgumentList.Add(argument);
        temporarySettings = settings.Host;
        process = Process.Start(start) ?? throw new InvalidOperationException("Could not start the temporary internet connector.");
        var launched = process;
        launched.Exited += (_, _) => { if (ReferenceEquals(process, launched)) ResetTemporary(); };
        launched.EnableRaisingEvents = true;
        _ = ReadAddress(process.StandardOutput, settings.Host);
        _ = ReadAddress(process.StandardError, settings.Host);
    }
    private async Task ReadAddress(StreamReader reader, HostSettings host)
    {
        try
        {
            while (await reader.ReadLineAsync() is { } line)
            {
                var match = Regex.Match(line, @"https://[a-z0-9-]+\.trycloudflare\.com\b", RegexOptions.CultureInvariant);
                if (!match.Success || !ReferenceEquals(temporarySettings, host)) continue;
                // Only the connector's explicit HTTPS quick-tunnel authority is accepted, never a wildcard Host.
                host.PublicOrigin = match.Value; AddressChanged?.Invoke(host.ClientOrigin);
            }
        }
        catch (IOException) { }
        catch (ObjectDisposedException) { }
    }
    private static async Task Drain(StreamReader reader)
    {
        try { while (await reader.ReadLineAsync() is not null) { } }
        catch (IOException) { }
        catch (ObjectDisposedException) { }
    }
    public void Stop()
    {
        ResetTemporary();
        try { if (IsRunning) { process!.Kill(true); process.WaitForExit(5000); } }
        catch (InvalidOperationException) { /* The process exited between observation and interruption. */ }
        process?.Dispose(); process = null;
    }
    private void ResetTemporary()
    {
        var temporary = Interlocked.Exchange(ref temporarySettings, null);
        if (temporary is not null) { temporary.PublicOrigin = ""; AddressChanged?.Invoke(temporary.Origin); }
    }
    public void Dispose() => Stop();
}
