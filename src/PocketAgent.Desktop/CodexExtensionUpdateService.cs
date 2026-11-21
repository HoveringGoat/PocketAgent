using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PocketAgent.Desktop;

public sealed record CodexExtensionUpdateResult(bool Updated, Version? InstalledVersion, Version? LatestVersion, string Message);

public static partial class CodexExtensionUpdateService
{
    private const string ExtensionId = "openai.chatgpt";
    private const string MarketplaceEndpoint = "https://marketplace.visualstudio.com/_apis/public/gallery/extensionquery";
    private static readonly HttpClient Client = new() { Timeout = TimeSpan.FromSeconds(20) };

    public static bool IsNewer(Version installed, Version latest) => latest > installed;

    public static async Task<CodexExtensionUpdateResult> CheckAsync(HostSettings host, bool install, CancellationToken cancellationToken = default)
    {
        if (!string.IsNullOrWhiteSpace(host.CodexExecutable))
            return new(false, null, null, "A Codex executable override is configured, so the VS Code Codex extension was not updated.");

        var extension = FindInstalledExtension(host.CodexExtensionsDirectory);
        if (extension is null)
            return new(false, null, null, "The VS Code Codex extension was not detected.");

        var latest = await FindLatestVersion(cancellationToken);
        if (!IsNewer(extension.Value.Version, latest))
            return new(false, extension.Value.Version, latest, $"VS Code Codex extension is up to date ({extension.Value.Version}).");
        if (!install)
            return new(false, extension.Value.Version, latest, $"VS Code Codex extension update {latest} is available (installed {extension.Value.Version}).");

        var cli = FindVsCodeCli();
        if (cli is null)
            return new(false, extension.Value.Version, latest, $"VS Code Codex extension update {latest} is available, but the VS Code command-line tool was not found.");
        await InstallLatest(cli, cancellationToken);
        return new(true, extension.Value.Version, latest, $"VS Code Codex extension update {latest} was installed. Restart the PocketAgent server to use it.");
    }

    private static async Task<Version> FindLatestVersion(CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, MarketplaceEndpoint)
        {
            Content = JsonContent.Create(new
            {
                filters = new[] { new { criteria = new[] { new { filterType = 7, value = "Microsoft.VisualStudio.Code" }, new { filterType = 8, value = ExtensionId } } } },
                flags = 103
            })
        };
        request.Headers.Add("Accept", "application/json;api-version=3.0-preview.1");
        using var response = await Client.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(cancellationToken));
        var version = document.RootElement.GetProperty("results")[0].GetProperty("extensions")[0].GetProperty("versions")[0].GetProperty("version").GetString();
        if (!Version.TryParse(version, out var parsed)) throw new InvalidOperationException("The VS Code Marketplace returned an invalid Codex extension version.");
        return parsed;
    }

    private static async Task InstallLatest(string cli, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo
        {
            FileName = cli.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) ? "cmd.exe" : cli,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        if (start.FileName.Equals("cmd.exe", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add($"/d /c \"\"{cli}\" --install-extension {ExtensionId} --force\"");
        else
        {
            start.ArgumentList.Add("--install-extension");
            start.ArgumentList.Add(ExtensionId);
            start.ArgumentList.Add("--force");
        }
        using var process = Process.Start(start) ?? throw new InvalidOperationException("VS Code could not be started to update the Codex extension.");
        await process.WaitForExitAsync(cancellationToken);
        if (process.ExitCode != 0)
            throw new InvalidOperationException("VS Code could not install the latest Codex extension.");
    }

    private static (Version Version, string Directory)? FindInstalledExtension(string configuredRoot)
    {
        var root = configuredRoot.Length > 0 ? configuredRoot : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".vscode", "extensions");
        if (!Directory.Exists(root)) return null;
        var architecture = System.Runtime.InteropServices.RuntimeInformation.OSArchitecture == System.Runtime.InteropServices.Architecture.Arm64 ? "windows-aarch64" : "windows-x86_64";
        return Directory.EnumerateDirectories(root, "openai.chatgpt-*")
            .Select(directory => (Directory: directory, Match: ExtensionVersion().Match(Path.GetFileName(directory))))
            .Where(entry => entry.Match.Success && !entry.Directory.Contains("insider", StringComparison.OrdinalIgnoreCase) && File.Exists(Path.Combine(entry.Directory, "bin", architecture, "codex.exe")))
            .Select(entry => (Version: Version.Parse(entry.Match.Groups[1].Value), entry.Directory))
            .OrderByDescending(entry => entry.Version)
            .FirstOrDefault() is { Version: not null } found ? found : null;
    }

    private static string? FindVsCodeCli()
    {
        var candidates = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator)
            .SelectMany(directory => new[] { Path.Combine(directory, "code.cmd"), Path.Combine(directory, "code.exe") })
            .Concat(new[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Microsoft VS Code", "bin", "code.cmd"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Microsoft VS Code", "bin", "code.cmd"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Microsoft VS Code Insiders", "bin", "code-insiders.cmd")
            });
        return candidates.FirstOrDefault(File.Exists);
    }

    [GeneratedRegex(@"^openai\.chatgpt-(\d+\.\d+\.\d+)(?:-|$)", RegexOptions.CultureInvariant)]
    private static partial Regex ExtensionVersion();
}
