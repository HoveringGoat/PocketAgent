using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace PocketAgent.Desktop;

public static class DesktopDiagnostics
{
    private const long MaxLogBytes = 2 * 1024 * 1024;
    private const int RetainedLogCount = 4;
    private static readonly object Gate = new();
    private static string? logPath;

    public static string LogPath => logPath ?? Path.Combine(GetLogDirectory(), "desktop.log");

    public static void Initialize()
    {
        lock (Gate)
        {
            if (logPath is not null) return;
            var directory = GetLogDirectory();
            Directory.CreateDirectory(directory);
            logPath = Path.Combine(directory, "desktop.log");
            RotateIfNeeded();
            WriteCore($"START version={typeof(DesktopDiagnostics).Assembly.GetName().Version} pid={Environment.ProcessId}");
        }
    }

    public static void Log(string message)
    {
        try
        {
            lock (Gate)
            {
                if (logPath is null) Initialize();
                WriteCore(Sanitize(message));
            }
        }
        catch
        {
            // Diagnostics must never take down the desktop application.
        }
    }

    public static void LogException(string source, Exception exception)
    {
        Log($"EXCEPTION source={source} {exception.GetType().FullName}: {exception}");
    }

    public static void LogShutdown(string reason) => Log($"STOP reason={reason}");

    private static string GetLogDirectory()
    {
        var overrideDirectory = Environment.GetEnvironmentVariable("POCKETAGENT_DESKTOP_LOG_DIRECTORY");
        return string.IsNullOrWhiteSpace(overrideDirectory)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PocketAgent", "logs")
            : Path.GetFullPath(overrideDirectory);
    }

    private static void RotateIfNeeded()
    {
        if (logPath is null || !File.Exists(logPath) || new FileInfo(logPath).Length <= MaxLogBytes) return;
        for (var index = RetainedLogCount - 1; index >= 1; index--)
        {
            var older = $"{logPath}.{index}";
            var newer = $"{logPath}.{index + 1}";
            if (File.Exists(older)) File.Move(older, newer, true);
        }
        File.Move(logPath, $"{logPath}.1", true);
    }

    private static void WriteCore(string message)
    {
        if (logPath is null) return;
        RotateIfNeeded();
        File.AppendAllText(logPath, $"{DateTimeOffset.Now:O} {message}{Environment.NewLine}", Encoding.UTF8);
    }

    private static string Sanitize(string value) => Regex.Replace(
        value,
        @"(?i)(authorization|token|password|secret|api[-_]?key)\s*([=:])\s*([^\s&,;]+)",
        "$1$2<redacted>");
}
