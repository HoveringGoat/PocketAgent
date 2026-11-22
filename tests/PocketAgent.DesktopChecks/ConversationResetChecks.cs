using System.IO;
using Microsoft.Data.Sqlite;
using PocketAgent;

internal static class ConversationResetChecks
{
    public static void Run(string root)
    {
        var data = Path.Combine(root, ".local", "conversation-reset-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(data);
        try
        {
            var store = new LocalStore(new HostSettings { DataDirectory = data });
            var paired = store.Pair("reset-check");
            store.SaveConversation("old", "Same name", data, "fixture-model", "high", paired.Device.Id);
            store.SetPinned("old", true);
            var source = store.Conversations(true).Single();
            store.Schedule("old", "pending work", DateTimeOffset.UtcNow.AddMinutes(10), "manual", paired.Device.Id);

            store.ResetConversation("old", "new", source);

            var rows = store.Conversations(true).ToDictionary(row => row.Id);
            if (!rows["old"].Archived || rows["new"].Archived || rows["new"].Title != "Same name" ||
                rows["new"].WorkingDirectory != data || rows["new"].Model != "fixture-model" ||
                rows["new"].ReasoningEffort != "high" || !rows["new"].Pinned)
                throw new InvalidOperationException("Reset must preserve conversation metadata on a fresh active conversation.");
            if (!store.Conversations(false, paired.Device.Id).Any(row => row.Id == "new") ||
                store.Conversations(false, paired.Device.Id).Any(row => row.Id == "old"))
                throw new InvalidOperationException("Reset must preserve device access only on the active replacement.");
            if (store.Scheduled("old").Any(job => job.Status == "pending"))
                throw new InvalidOperationException("Reset must cancel pending schedules on the archived conversation.");
            Console.WriteLine("PASS conversation reset preserves metadata/access and archives the old transcript.");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(data)) Directory.Delete(data, true);
        }
    }
}
