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

        Console.WriteLine("PASS last selected desktop conversation restore and stale-selection cleanup.");
    }

    private static ConversationSummary Summary(string id) =>
        new(id, id, DateTimeOffset.UtcNow, "saved", "repo", "repo", "host", null, false);
}
