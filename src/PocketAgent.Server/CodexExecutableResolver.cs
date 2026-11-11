using System.Text.RegularExpressions;

namespace PocketAgent;

public sealed record CodexExecutable(string Path, string Source);

public static partial class CodexExecutableResolver
{
    public static CodexExecutable Resolve(HostSettings settings)
    {
        if (!string.IsNullOrWhiteSpace(settings.CodexExecutable))
        {
            var path = Path.GetFullPath(settings.CodexExecutable);
            if (!File.Exists(path))
                throw new InvalidOperationException("The configured Codex executable does not exist.");
            return new(path, "Explicit override");
        }
        var root = settings.CodexExtensionsDirectory.Length > 0 ? settings.CodexExtensionsDirectory :
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".vscode", "extensions");
        if (OperatingSystem.IsWindows() && Directory.Exists(root))
        {
            var architecture = System.Runtime.InteropServices.RuntimeInformation.OSArchitecture == System.Runtime.InteropServices.Architecture.Arm64
                ? "windows-aarch64" : "windows-x86_64";
            var candidates = Directory.EnumerateDirectories(root, "openai.chatgpt-*")
                .Select(directory => (Directory: directory, Match: ExtensionVersion().Match(Path.GetFileName(directory))))
                .Where(entry => entry.Match.Success && !entry.Directory.Contains("insider", StringComparison.OrdinalIgnoreCase))
                .Select(entry => (entry.Directory, Version: Version.Parse(entry.Match.Groups[1].Value)))
                .OrderByDescending(entry => entry.Version).ThenBy(entry => entry.Directory, StringComparer.OrdinalIgnoreCase);
            foreach (var candidate in candidates)
            {
                var binary = Path.Combine(candidate.Directory, "bin", architecture, "codex.exe");
                if (File.Exists(binary))
                    return new(binary, $"VS Code extension {candidate.Version}");
            }
        }
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            var binary = Path.Combine(directory, OperatingSystem.IsWindows() ? "codex.exe" : "codex");
            if (File.Exists(binary)) return new(Path.GetFullPath(binary), "Native CLI on PATH");
            if (OperatingSystem.IsWindows())
            {
                var npm = Path.Combine(directory, "node_modules", "@openai", "codex", "node_modules", "@openai", "codex-win32-x64", "vendor", "x86_64-pc-windows-msvc", "bin", "codex.exe");
                if (File.Exists(npm)) return new(Path.GetFullPath(npm), "npm native CLI");
            }
        }
        throw new InvalidOperationException("Install the Codex VS Code extension or select a native Codex executable in Settings.");
    }

    [GeneratedRegex(@"^openai\.chatgpt-(\d+\.\d+\.\d+)(?:-|$)", RegexOptions.CultureInvariant)]
    private static partial Regex ExtensionVersion();
}
