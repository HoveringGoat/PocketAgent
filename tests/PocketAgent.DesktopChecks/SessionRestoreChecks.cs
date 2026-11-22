using System.IO;
using System.Reflection;
using PocketAgent;
using PocketAgent.Desktop;

internal static class SessionRestoreChecks
{
    public static void Run()
    {
        var window = new MainWindow();
        var settings = (DesktopSettings)typeof(MainWindow).GetField("settings", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window)!;
        settings.LastSelectedConversationId = "restore-me";
        settings.Save();
        if (DesktopSettings.Load().LastSelectedConversationId != "restore-me")
            throw new InvalidOperationException("The last selected conversation must survive desktop settings persistence.");

        var restore = typeof(MainWindow).GetMethod("RestoreLastSelectedConversation", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var restored = restore.Invoke(window, [new[]
        {
            Summary("other"),
            Summary("restore-me")
        }]);
        if (!Equals(restored, "restore-me"))
            throw new InvalidOperationException("Desktop startup must restore the last selected conversation when it still exists.");

        settings.LastSelectedConversationId = "archived-or-deleted";
        var missing = restore.Invoke(window, [new[] { Summary("other") }]);
        if (missing is not null || settings.LastSelectedConversationId is not null)
            throw new InvalidOperationException("Desktop startup must clear a saved conversation that is no longer available.");
        if (DesktopSettings.Load().LastSelectedConversationId is not null)
            throw new InvalidOperationException("Clearing a stale conversation selection must be persisted.");

        settings.Host.RepositoryPath = Directory.GetCurrentDirectory();
        settings.StartServerOnLaunch = false;
        settings.CheckUpdatesOnStartup = false;
        var navigation = (System.Windows.Controls.TabControl)window.FindName("Navigation")!;
        navigation.SelectedIndex = 2;
        typeof(MainWindow).GetMethod("OpenSessionsOnStartup", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, null);
        if (navigation.SelectedIndex != 0)
            throw new InvalidOperationException("Desktop startup must open the Sessions page when a repository is configured.");
        var ready = (Task)typeof(MainWindow).GetMethod("WindowReady", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, null)!;
        ready.GetAwaiter().GetResult();
        if (navigation.SelectedIndex != 0)
            throw new InvalidOperationException("Desktop startup must open the Sessions page when a repository is configured.");

        window.Left = 123;
        window.Top = 234;
        window.Width = 987;
        window.Height = 654;
        var savePlacement = typeof(MainWindow).GetMethod("SaveWindowPlacement", BindingFlags.NonPublic | BindingFlags.Instance)!;
        savePlacement.Invoke(window, null);
        var savedPlacement = DesktopSettings.Load();
        if (savedPlacement.WindowLeft != 123 || savedPlacement.WindowTop != 234 || savedPlacement.WindowWidth != 987 || savedPlacement.WindowHeight != 654)
            throw new InvalidOperationException("Desktop window placement must be persisted.");

        var restoredWindow = new MainWindow();
        if (restoredWindow.Left != 123 || restoredWindow.Top != 234 || restoredWindow.Width != 987 || restoredWindow.Height != 654)
            throw new InvalidOperationException("Desktop window placement must be restored on startup.");

        Console.WriteLine("PASS last selected desktop conversation restore and stale-selection cleanup.");
    }

    private static ConversationSummary Summary(string id) =>
        new(id, id, DateTimeOffset.UtcNow, "saved", "repo", "repo", "host", null, false);
}
