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
    private const int ExtensionNameFilter = 7;
    private const int ProductTargetFilter = 8;
    private const int IncludeVersions = 1;
    private const int IncludeVersionProperties = 16;
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
        DesktopDiagnostics.Log($"CODEX extension update installed={extension.Value.Version} marketplace={latest}");
        if (!IsNewer(extension.Value.Version, latest))
            return new(false, extension.Value.Version, latest, $"VS Code Codex extension is up to date ({extension.Value.Version}).");
        if (!install)
            return new(false, extension.Value.Version, latest, $"VS Code Codex extension update {latest} is available (installed {extension.Value.Version}).");

        var cli = FindVsCodeCli();
        if (cli is null)
            return new(false, extension.Value.Version, latest, $"VS Code Codex extension update {latest} is available, but the VS Code command-line tool was not found.");
        await InstallVersion(cli, latest, cancellationToken);
        return new(true, extension.Value.Version, latest, $"VS Code Codex extension update {latest} was installed. Restart the PocketAgent server to use it.");
    }

    private static async Task<Version> FindLatestVersion(CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, MarketplaceEndpoint)
        {
            Content = JsonContent.Create(new
            {
                filters = new[] {
                    new {
                        criteria = new[] {
                            new { filterType = ExtensionNameFilter, value = ExtensionId },
                            new { filterType = ProductTargetFilter, value = "Microsoft.VisualStudio.Code" }
                        },
                        pageNumber = 1,
                        pageSize = 1
                    }
                },
                flags = IncludeVersions | IncludeVersionProperties
            })
        };
        request.Headers.Add("Accept", "application/json;api-version=3.0-preview.1");
        using var response = await Client.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
        var platform = System.Runtime.InteropServices.RuntimeInformation.OSArchitecture == System.Runtime.InteropServices.Architecture.Arm64 ? "win32-arm64" : "win32-x64";
        return ReadLatestVersion(document.RootElement, platform);
    }

    private static Version ReadLatestVersion(JsonElement response, string platform)
    {
        var result = ReadNonEmptyArray(response, "results")[0];
        var extension = ReadNonEmptyArray(result, "extensions")[0];
        var versions = ReadNonEmptyArray(extension, "versions");
        Version? latest = null;
        foreach (var entry in versions.EnumerateArray())
        {
            if (!Version.TryParse(ReadString(entry, "version"), out var candidate)) continue;
            var target = ReadString(entry, "targetPlatform");
            if (target is not (null or "universal") && target != platform) continue;
            if (IsPreRelease(entry)) continue;
            if (latest is null || candidate > latest) latest = candidate;
        }
        return latest ?? throw new JsonException($"The VS Code Marketplace returned no stable Codex version for {platform}.");
    }

    private static JsonElement ReadNonEmptyArray(JsonElement parent, string name)
    {
        if (parent.ValueKind != JsonValueKind.Object || !parent.TryGetProperty(name, out var array) || array.ValueKind != JsonValueKind.Array)
            throw new JsonException($"The VS Code Marketplace response has a missing or invalid '{name}' list.");
        if (array.GetArrayLength() == 0)
            throw new JsonException($"The VS Code Marketplace response has an empty '{name}' list.");
        return array;
    }

    private static string? ReadString(JsonElement parent, string name) =>
        parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool IsPreRelease(JsonElement version)
    {
        if (!version.TryGetProperty("properties", out var properties) || properties.ValueKind != JsonValueKind.Array) return false;
        return properties.EnumerateArray().Any(property =>
            ReadString(property, "key") == "Microsoft.VisualStudio.Code.PreRelease" &&
            string.Equals(ReadString(property, "value"), "true", StringComparison.OrdinalIgnoreCase));
    }

    private static async Task InstallVersion(string cli, Version version, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo
        {
            FileName = cli.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) ? "cmd.exe" : cli,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        var extension = $"{ExtensionId}@{version}";
        if (start.FileName.Equals("cmd.exe", StringComparison.OrdinalIgnoreCase))
            start.Arguments = $"/d /s /c \"\"{cli}\" --install-extension {extension} --force\"";
        else
        {
            start.ArgumentList.Add("--install-extension");
            start.ArgumentList.Add(extension);
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
