using Microsoft.Extensions.Logging;
using System.Text;
using System.Text.RegularExpressions;

namespace PocketAgent;

public static class ServerDiagnostics
{
    private const long MaxLogBytes = 4 * 1024 * 1024;
    private const int RetainedLogCount = 4;
    private static readonly object Gate = new();
    private static string? logPath;

    public static string LogPath => logPath ?? Path.Combine(GetLogDirectory(), "server.log");

    public static void Initialize()
    {
        lock (Gate)
        {
            if (logPath is not null) return;
            var directory = GetLogDirectory();
            Directory.CreateDirectory(directory);
            logPath = Path.Combine(directory, "server.log");
            RotateIfNeeded();
            WriteCore($"START version={BuildInfo.Version} pid={Environment.ProcessId}");
        }
    }

    public static void Log(string category, LogLevel level, string message, Exception? exception = null)
    {
        try
        {
            lock (Gate)
            {
                if (logPath is null) Initialize();
                var suffix = exception is null
                    ? ""
                    : $" exception={exception.GetType().Name} detail={Limit(exception.Message, 1200)}";
                WriteCore($"{level.ToString().ToUpperInvariant()} category={category} {message}{suffix}");
            }
        }
        catch
        {
            // Diagnostics must never take down the server.
        }
    }

    public static void Stop(string reason) => Log("server", LogLevel.Information, $"STOP reason={reason}");

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

    private static string Limit(string value, int length) => value.Length <= length ? value : value[..length] + "...";
}

public sealed class PocketAgentLoggerProvider : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => new PocketAgentLogger(categoryName);
    public void Dispose() { }

    private sealed class PocketAgentLogger(string category) : ILogger
    {
        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;
        public bool IsEnabled(LogLevel logLevel) => logLevel >= (category.StartsWith("Microsoft.", StringComparison.Ordinal) ? LogLevel.Warning : LogLevel.Information);
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            ServerDiagnostics.Log(category, logLevel, formatter(state, null), exception);
        }

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();
            public void Dispose() { }
        }
    }
}
