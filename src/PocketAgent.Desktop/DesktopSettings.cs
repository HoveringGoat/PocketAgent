using System.IO;
using System.Text.Json;

namespace PocketAgent.Desktop;

public sealed class DesktopSettings
{
    public HostSettings Host { get; set; } = new();
    public bool CheckUpdatesOnStartup { get; set; } = true;
    public bool CheckUpdatesPeriodically { get; set; } = true;
    public bool AutomaticUpdates { get; set; }
    public bool StartServerOnLaunch { get; set; } = true;
    public bool LaunchOnWindowsStartup { get; set; }
    public string UiScale { get; set; } = "comfortable";
    public static string DirectoryPath => Environment.GetEnvironmentVariable("POCKETAGENT_DESKTOP_DATA") is { Length: > 0 } configured ? Path.GetFullPath(configured) :
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PocketAgent");
    public static string FilePath => Path.Combine(DirectoryPath, "desktop.json");
    public static DesktopSettings Load()
    {
        if (!File.Exists(FilePath)) return new();
        return JsonSerializer.Deserialize<DesktopSettings>(File.ReadAllText(FilePath)) ?? new();
    }
    public void Save()
    {
        Directory.CreateDirectory(DirectoryPath);
        var temporary = FilePath + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        for (var attempt = 0; ; attempt++)
        {
            try { File.Move(temporary, FilePath, true); break; }
            catch (Exception exception) when (attempt < 4 && exception is IOException or UnauthorizedAccessException)
            {
                // Brief Windows file-sharing/scanner locks must not discard an otherwise valid save.
                Thread.Sleep(100);
            }
        }
    }
}
