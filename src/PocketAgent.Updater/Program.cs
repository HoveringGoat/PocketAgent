using System.Diagnostics;
using System.Net.Http.Headers;
using System.Security.Cryptography;

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
try
{
    WriteLog(logFile, $"Starting update to {arguments.GetValueOrDefault("version", "unknown")}.");
    Directory.CreateDirectory(staging);
    using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
    client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("PocketAgent.Updater", "0.2.0"));
    var installer = Path.Combine(staging, "PocketAgent-Setup.exe");
    await using (var input = await client.GetStreamAsync(arguments["asset-url"]))
    await using (var output = File.Create(installer)) await input.CopyToAsync(output);
    if (arguments.TryGetValue("sha256-url", out var checksumUrl))
    {
        var checksumText = await client.GetStringAsync(checksumUrl);
        var expected = checksumText.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        await using var installerStream = File.OpenRead(installer);
        var actual = Convert.ToHexString(await SHA256.HashDataAsync(installerStream)).ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(expected) || !CryptographicOperations.FixedTimeEquals(Convert.FromHexString(expected), Convert.FromHexString(actual)))
            return Fail("The downloaded desktop installer failed checksum verification.", logFile);
    }
    WriteLog(logFile, "Package downloaded and checksum verified.");
    await WaitForExit(int.Parse(arguments["wait-pid"], System.Globalization.CultureInfo.InvariantCulture));
    parentExited = true;
    WriteLog(logFile, "Parent application exited.");
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
    if (installerProcess.ExitCode != 0) return Fail($"The desktop installer exited with code {installerProcess.ExitCode}.", logFile);
    WriteLog(logFile, "Desktop installer completed.");
    var restart = new ProcessStartInfo(restartPath)
    {
        UseShellExecute = false,
        CreateNoWindow = true,
        WorkingDirectory = Path.GetDirectoryName(restartPath) ?? installRoot
    };
    if (!string.IsNullOrWhiteSpace(healthFile)) { restart.ArgumentList.Add("--health-file"); restart.ArgumentList.Add(healthFile); }
    restart.ArgumentList.Add("--update-version"); restart.ArgumentList.Add(arguments.GetValueOrDefault("version", "unknown"));
    var process = Process.Start(restart);
    if (process is null) throw new InvalidOperationException("The updated desktop could not be started.");
    WriteLog(logFile, "Updated application started; waiting for health check.");
    var healthy = await WaitForFile(arguments.GetValueOrDefault("health-file"), TimeSpan.FromSeconds(30));
    if (!healthy)
    {
        try { if (!process.HasExited) process.Kill(true); } catch { }
        return Fail("The installed update failed its startup check.", logFile);
    }
    WriteLog(logFile, "Update completed successfully.");
    return 0;
}
catch (Exception exception)
{
    if (parentExited) TryStart(restartPath);
    return Fail($"Installer update failed: {exception}", logFile);
}
finally
{
    try { if (Directory.Exists(staging)) Directory.Delete(staging, true); } catch { }
    try { if (!string.IsNullOrWhiteSpace(healthFile)) File.Delete(healthFile); } catch { }
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
