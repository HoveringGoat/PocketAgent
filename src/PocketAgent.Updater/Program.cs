using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http.Headers;
using System.Security.Cryptography;

var arguments = Parse(args);
foreach (var required in new[] { "asset-url", "install-root", "restart-path", "wait-pid" })
    if (!arguments.ContainsKey(required)) return Fail($"Missing updater argument: {required}.");

var installRoot = Path.GetFullPath(arguments["install-root"]);
var restartPath = Path.GetFullPath(arguments["restart-path"]);
var staging = Path.Combine(Path.GetTempPath(), "PocketAgent-update-" + Guid.NewGuid().ToString("N"));
var archive = Path.Combine(staging, "package.zip");
var extracted = Path.Combine(staging, "extracted");
var backup = installRoot.TrimEnd(Path.DirectorySeparatorChar) + ".previous-" + DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmss");
try
{
    Directory.CreateDirectory(staging);
    using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
    client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("PocketAgent.Updater", "0.2.0"));
    await using (var input = await client.GetStreamAsync(arguments["asset-url"]))
    await using (var output = File.Create(archive)) await input.CopyToAsync(output);
    if (arguments.TryGetValue("sha256-url", out var checksumUrl))
    {
        var checksumText = await client.GetStringAsync(checksumUrl);
        var expected = checksumText.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        var actual = Convert.ToHexString(await SHA256.HashDataAsync(File.OpenRead(archive))).ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(expected) || !CryptographicOperations.FixedTimeEquals(Convert.FromHexString(expected), Convert.FromHexString(actual)))
            return Fail("The downloaded desktop package failed checksum verification.");
    }
    await WaitForExit(int.Parse(arguments["wait-pid"], System.Globalization.CultureInfo.InvariantCulture));
    ZipFile.ExtractToDirectory(archive, extracted);
    var executable = Directory.EnumerateFiles(extracted, "PocketAgent.Desktop.exe", SearchOption.AllDirectories).SingleOrDefault();
    if (executable is null) return Fail("The update package did not contain the desktop executable.");
    var replacement = Directory.GetParent(executable)!.FullName;
    Directory.Move(installRoot, backup);
    Directory.Move(replacement, installRoot);
    var process = Process.Start(new ProcessStartInfo(restartPath) { UseShellExecute = true });
    if (process is null) throw new InvalidOperationException("The updated desktop could not be started.");
    var healthy = await WaitForFile(arguments.GetValueOrDefault("health-file"), TimeSpan.FromSeconds(30));
    if (!healthy)
    {
        try { if (!process.HasExited) process.Kill(true); } catch { }
        Directory.Move(installRoot, installRoot + ".failed-" + DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmss"));
        Directory.Move(backup, installRoot);
        Process.Start(new ProcessStartInfo(restartPath) { UseShellExecute = true });
        return Fail("The update failed its startup check and was rolled back.");
    }
    return 0;
}
catch (Exception exception) { return Fail($"Update failed: {exception.Message}"); }
finally { try { if (Directory.Exists(staging)) Directory.Delete(staging, true); } catch { } }

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

static int Fail(string message) { Console.Error.WriteLine(message); return 1; }
