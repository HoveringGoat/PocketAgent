using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;

namespace PocketAgent.Desktop;

public sealed record DesktopUpdate(Version Version, string Tag, Uri Asset, string? Sha256, Uri ReleasePage, string Notes);

public static class UpdateService
{
    public const string ReleaseListEndpoint = "https://api.github.com/repos/HoveringGoat/PocketAgent/releases?per_page=30";
    private static readonly HttpClient Client = CreateClient();

    public static async Task<DesktopUpdate?> CheckAsync(CancellationToken cancellationToken = default)
    {
        using var response = await Client.GetAsync(ReleaseListEndpoint, cancellationToken);
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(cancellationToken));
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Array) return null;
        var selected = default(JsonElement);
        var selectedVersion = new Version(0, 0, 0);
        foreach (var release in root.EnumerateArray())
        {
            var candidateTag = release.TryGetProperty("tag_name", out var tagProperty) ? tagProperty.GetString() ?? "" : "";
            if (!TryDesktopVersion(candidateTag, out var candidateVersion)) continue;
            if (selected.ValueKind == JsonValueKind.Undefined || candidateVersion > selectedVersion) { selected = release; selectedVersion = candidateVersion; }
        }
        if (selected.ValueKind == JsonValueKind.Undefined) return null;
        root = selected;
        var tag = root.GetProperty("tag_name").GetString() ?? "";
        if (!TryDesktopVersion(tag, out var version) || version <= Version.Parse(BuildInfo.Version)) return null;
        var assets = root.GetProperty("assets").EnumerateArray().ToArray();
        var desktop = assets.FirstOrDefault(asset => asset.GetProperty("name").GetString()?.EndsWith("-desktop.zip", StringComparison.OrdinalIgnoreCase) == true);
        if (desktop.ValueKind == JsonValueKind.Undefined) return null;
        var checksum = assets.FirstOrDefault(asset => asset.GetProperty("name").GetString()?.Equals(desktop.GetProperty("name").GetString() + ".sha256", StringComparison.OrdinalIgnoreCase) == true);
        var assetUrl = desktop.GetProperty("browser_download_url").GetString();
        if (!Uri.TryCreate(assetUrl, UriKind.Absolute, out var assetUri)) return null;
        var releaseUrl = root.GetProperty("html_url").GetString();
        if (!Uri.TryCreate(releaseUrl, UriKind.Absolute, out var releasePage)) releasePage = new Uri("https://github.com/HoveringGoat/PocketAgent/releases");
        var notes = root.TryGetProperty("body", out var body) && body.ValueKind == JsonValueKind.String ? body.GetString() ?? string.Empty : string.Empty;
        return new DesktopUpdate(version, tag, assetUri, checksum.ValueKind == JsonValueKind.Undefined ? null : checksum.GetProperty("browser_download_url").GetString(), releasePage, notes.Trim());
    }

    public static Process? Launch(DesktopUpdate update, int parentProcessId)
    {
        var updater = Path.Combine(AppContext.BaseDirectory, "PocketAgent.Updater.exe");
        var executable = Environment.ProcessPath;
        if (!File.Exists(updater) || string.IsNullOrWhiteSpace(executable)) return null;
        try
        {
            var launchCopy = Path.Combine(Path.GetTempPath(), "PocketAgent-updater-" + Guid.NewGuid().ToString("N") + ".exe");
            File.Copy(updater, launchCopy, overwrite: false);
            var logFile = Path.Combine(Path.GetTempPath(), "PocketAgent-update.log");
            var info = new ProcessStartInfo(launchCopy)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(launchCopy) ?? Path.GetTempPath()
            };
            info.ArgumentList.Add("--asset-url"); info.ArgumentList.Add(update.Asset.ToString());
            if (update.Sha256 is { Length: > 0 } checksum) { info.ArgumentList.Add("--sha256-url"); info.ArgumentList.Add(checksum); }
            info.ArgumentList.Add("--install-root"); info.ArgumentList.Add(AppContext.BaseDirectory);
            info.ArgumentList.Add("--restart-path"); info.ArgumentList.Add(executable);
            info.ArgumentList.Add("--wait-pid"); info.ArgumentList.Add(parentProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
            info.ArgumentList.Add("--version"); info.ArgumentList.Add(update.Version.ToString(3));
            info.ArgumentList.Add("--health-file"); info.ArgumentList.Add(Path.Combine(Path.GetTempPath(), "PocketAgent-update-health-" + Guid.NewGuid().ToString("N")));
            info.ArgumentList.Add("--log-file"); info.ArgumentList.Add(logFile);
            return Process.Start(info);
        }
        catch { return null; }
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("PocketAgent", BuildInfo.Version));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return client;
    }

    private static bool TryVersion(string tag, out Version version)
    {
        var value = tag.StartsWith('v') ? tag[1..] : tag;
        return Version.TryParse(value, out version!);
    }

    private static bool TryDesktopVersion(string tag, out Version version)
    {
        if (!tag.StartsWith("desktop-v", StringComparison.OrdinalIgnoreCase))
        {
            version = new Version(0, 0, 0);
            return false;
        }
        return TryVersion(tag["desktop-".Length..], out version);
    }
}
