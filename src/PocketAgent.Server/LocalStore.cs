using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;

namespace PocketAgent;

public sealed record Device(string Id, string Name, DateTimeOffset ExpiresAt);
public sealed record ConversationReference(string Id, string Title, DateTimeOffset CreatedAt, string WorkingDirectory, string? Model = null, bool Archived = false, string? ReasoningEffort = null, bool Pinned = false, DateTimeOffset? LastMessageAt = null);
public sealed record ScheduledMessage(string Id, string ConversationId, string Text, DateTimeOffset DueAt, DateTimeOffset NextAttemptAt,
    string Status, string Reason, string? Error, [property: System.Text.Json.Serialization.JsonIgnore] string? DeviceId);
public sealed record MessageDelivery(string ConversationId, string ClientMessageId, string Text, string Status, string? ScheduleId, string? Error);

public sealed class LocalStore
{
    private readonly string connectionString;

    public LocalStore(HostSettings settings)
    {
        connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(settings.DataDirectory, "pocketagent.db"),
            Mode = SqliteOpenMode.ReadWriteCreate
        }.ToString();
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS devices (id TEXT PRIMARY KEY, name TEXT NOT NULL, token_hash TEXT NOT NULL UNIQUE, expires_at INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS conversations (id TEXT PRIMARY KEY, title TEXT NOT NULL, created_at INTEGER NOT NULL, repository TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS scheduled_messages (id TEXT PRIMARY KEY, conversation_id TEXT NOT NULL, text TEXT NOT NULL, due_at INTEGER NOT NULL, next_at INTEGER NOT NULL, status TEXT NOT NULL, reason TEXT NOT NULL, error TEXT, device_id TEXT);
            CREATE TABLE IF NOT EXISTS message_deliveries (conversation_id TEXT NOT NULL, client_message_id TEXT NOT NULL, text TEXT NOT NULL, status TEXT NOT NULL, schedule_id TEXT, error TEXT, created_at INTEGER NOT NULL, PRIMARY KEY(conversation_id,client_message_id));
            CREATE TABLE IF NOT EXISTS message_times (conversation_id TEXT NOT NULL, item_id TEXT NOT NULL, timestamp INTEGER NOT NULL, PRIMARY KEY(conversation_id,item_id));
            CREATE TABLE IF NOT EXISTS turn_times (conversation_id TEXT NOT NULL, turn_id TEXT NOT NULL, started INTEGER, completed INTEGER, duration INTEGER, PRIMARY KEY(conversation_id,turn_id));
            """;
        command.ExecuteNonQuery();
        command.CommandText = "PRAGMA table_info(conversations)";
        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var reader = command.ExecuteReader()) while (reader.Read()) columns.Add(reader.GetString(1));
        foreach (var (name, definition) in new[] { ("model", "TEXT"), ("archived", "INTEGER NOT NULL DEFAULT 0"), ("reasoning_effort", "TEXT"), ("pinned", "INTEGER NOT NULL DEFAULT 0"), ("last_message_at", "INTEGER") })
            if (!columns.Contains(name))
            {
                command.CommandText = $"ALTER TABLE conversations ADD COLUMN {name} {definition}";
                command.ExecuteNonQuery();
            }
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(connectionString);
        connection.Open();
        return connection;
    }

    public MessageDelivery? Delivery(string conversationId, string clientMessageId)
    {
        using var connection = Open(); using var command = connection.CreateCommand();
        command.CommandText = "SELECT conversation_id,client_message_id,text,status,schedule_id,error FROM message_deliveries WHERE conversation_id=$conversation AND client_message_id=$client";
        command.Parameters.AddWithValue("$conversation", conversationId); command.Parameters.AddWithValue("$client", clientMessageId);
        using var reader = command.ExecuteReader();
        return reader.Read() ? new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.IsDBNull(4) ? null : reader.GetString(4), reader.IsDBNull(5) ? null : reader.GetString(5)) : null;
    }

    public bool BeginDelivery(string conversationId, string clientMessageId, string text)
    {
        using var connection = Open(); using var command = connection.CreateCommand();
        command.CommandText = "INSERT OR IGNORE INTO message_deliveries(conversation_id,client_message_id,text,status,created_at) VALUES($conversation,$client,$text,'pending',$created)";
        command.Parameters.AddWithValue("$conversation", conversationId); command.Parameters.AddWithValue("$client", clientMessageId); command.Parameters.AddWithValue("$text", text); command.Parameters.AddWithValue("$created", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        return command.ExecuteNonQuery() == 1;
    }

    public void UpdateDelivery(string conversationId, string clientMessageId, string status, string? scheduleId = null, string? error = null)
    {
        using var connection = Open(); using var command = connection.CreateCommand();
        command.CommandText = "UPDATE message_deliveries SET status=$status,schedule_id=COALESCE($schedule,schedule_id),error=$error WHERE conversation_id=$conversation AND client_message_id=$client";
        command.Parameters.AddWithValue("$conversation", conversationId); command.Parameters.AddWithValue("$client", clientMessageId); command.Parameters.AddWithValue("$status", status); command.Parameters.AddWithValue("$schedule", (object?)scheduleId ?? DBNull.Value); command.Parameters.AddWithValue("$error", (object?)error ?? DBNull.Value);
        command.ExecuteNonQuery();
    }

    public (Device Device, string Token) Pair(string name)
    {
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var device = new Device(Guid.NewGuid().ToString("N"), name, DateTimeOffset.UtcNow.AddDays(30));
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO devices VALUES ($id, $name, $hash, $expires)";
        command.Parameters.AddWithValue("$id", device.Id);
        command.Parameters.AddWithValue("$name", name);
        command.Parameters.AddWithValue("$hash", Hash(token));
        command.Parameters.AddWithValue("$expires", device.ExpiresAt.ToUnixTimeSeconds());
        command.ExecuteNonQuery();
        return (device, token);
    }

    public Device? FindDevice(string token)
    {
        if (token.Length != 64)
            return null;
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, name, expires_at FROM devices WHERE token_hash=$hash AND expires_at>$now";
        command.Parameters.AddWithValue("$hash", Hash(token));
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        using var reader = command.ExecuteReader();
        return reader.Read() ? new(reader.GetString(0), reader.GetString(1), DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(2))) : null;
    }

    public string[] ActiveDeviceIds()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id FROM devices WHERE expires_at>$now";
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        using var reader = command.ExecuteReader();
        var ids = new List<string>();
        while (reader.Read())
            ids.Add(reader.GetString(0));
        return [.. ids];
    }

    public void RevokeAll()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE scheduled_messages SET status='canceled', error='Device access revoked.' WHERE device_id IS NOT NULL AND status='pending'; DELETE FROM devices";
        command.ExecuteNonQuery();
    }

    public Device[] Devices()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, name, expires_at FROM devices WHERE expires_at>$now ORDER BY name";
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        using var reader = command.ExecuteReader();
        var devices = new List<Device>();
        while (reader.Read()) devices.Add(new(reader.GetString(0), reader.GetString(1), DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(2))));
        return devices.ToArray();
    }

    public void RenameConversation(string id, string title)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE conversations SET title=$title WHERE id=$id AND archived=0";
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$title", title);
        if (command.ExecuteNonQuery() != 1) throw new ApiException(404, "Conversation not found.");
    }

    public void Revoke(string id)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE scheduled_messages SET status='canceled', error='Device access revoked.' WHERE device_id=$id AND status='pending'; DELETE FROM devices WHERE id=$id";
        command.Parameters.AddWithValue("$id", id);
        command.ExecuteNonQuery();
    }

    public void SaveConversation(string id, string title, string workingDirectory, string? model, string? reasoningEffort)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO conversations (id,title,created_at,repository,model,reasoning_effort) VALUES ($id,$title,$created,$repository,$model,$reasoning)";
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$title", title);
        command.Parameters.AddWithValue("$created", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        command.Parameters.AddWithValue("$repository", workingDirectory);
        command.Parameters.AddWithValue("$model", (object?)model ?? DBNull.Value);
        command.Parameters.AddWithValue("$reasoning", (object?)reasoningEffort ?? DBNull.Value);
        command.ExecuteNonQuery();
    }

    public ConversationReference[] Conversations(bool includeArchived = false)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id,title,created_at,repository,model,archived,reasoning_effort,pinned,last_message_at FROM conversations WHERE $include=1 OR archived=0 ORDER BY created_at DESC,id";
        command.Parameters.AddWithValue("$include", includeArchived ? 1 : 0);
        using var reader = command.ExecuteReader();
        var rows = new List<ConversationReference>();
        while (reader.Read())
            rows.Add(new(reader.GetString(0), reader.GetString(1), DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(2)), reader.GetString(3), reader.IsDBNull(4) ? null : reader.GetString(4), reader.GetInt64(5) != 0, reader.IsDBNull(6) ? null : reader.GetString(6), reader.GetInt64(7) != 0, reader.IsDBNull(8) ? null : DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(8))));
        return [.. rows];
    }

    public void SetPinned(string id, bool pinned)
    {
        using var connection = Open(); using var command = connection.CreateCommand();
        command.CommandText = "UPDATE conversations SET pinned=$pinned WHERE id=$id AND archived=0";
        command.Parameters.AddWithValue("$id", id); command.Parameters.AddWithValue("$pinned", pinned ? 1 : 0);
        if (command.ExecuteNonQuery() != 1) throw new ApiException(404, "Conversation not found.");
    }

    public string[] ArchiveInactive(DateTimeOffset cutoff, IReadOnlyCollection<string> activeIds)
    {
        using var connection = Open(); using var transaction = connection.BeginTransaction(); using var command = connection.CreateCommand();
        command.Transaction = transaction;
        var excluded = activeIds.Select((id, index) => (Name: "$active" + index, Id: id)).ToArray();
        var exclusion = excluded.Length == 0 ? "" : $" AND id NOT IN ({string.Join(',', excluded.Select(item => item.Name))})";
        command.CommandText = $"SELECT id FROM conversations WHERE archived=0 AND pinned=0 AND COALESCE(last_message_at,created_at)<$cutoff {exclusion} AND NOT EXISTS (SELECT 1 FROM scheduled_messages WHERE conversation_id=conversations.id AND status IN ('pending','dispatching'))";
        command.Parameters.AddWithValue("$cutoff", cutoff.ToUnixTimeMilliseconds());
        foreach (var item in excluded) command.Parameters.AddWithValue(item.Name, item.Id);
        var ids = new List<string>();
        using (var reader = command.ExecuteReader()) while (reader.Read()) ids.Add(reader.GetString(0));
        foreach (var id in ids)
        {
            command.CommandText = "UPDATE conversations SET archived=1 WHERE id=$id AND archived=0";
            command.Parameters.Clear(); command.Parameters.AddWithValue("$id", id); command.ExecuteNonQuery();
            command.CommandText = "UPDATE scheduled_messages SET status='canceled',error='Conversation archived.' WHERE conversation_id=$id AND status='pending'";
            command.Parameters.Clear(); command.Parameters.AddWithValue("$id", id); command.ExecuteNonQuery();
        }
        transaction.Commit();
        return [.. ids];
    }

    public void SetModel(string id, string model)
    {
        using var connection = Open(); using var command = connection.CreateCommand();
        command.CommandText = "UPDATE conversations SET model=$model WHERE id=$id AND archived=0";
        command.Parameters.AddWithValue("$id", id); command.Parameters.AddWithValue("$model", model);
        if (command.ExecuteNonQuery() != 1) throw new ApiException(404, "Conversation not found.");
    }

    public void SetReasoningEffort(string id, string? effort)
    {
        using var connection = Open(); using var command = connection.CreateCommand();
        command.CommandText = "UPDATE conversations SET reasoning_effort=$reasoning WHERE id=$id AND archived=0";
        command.Parameters.AddWithValue("$id", id); command.Parameters.AddWithValue("$reasoning", (object?)effort ?? DBNull.Value);
        if (command.ExecuteNonQuery() != 1) throw new ApiException(404, "Conversation not found.");
    }

    public void SetArchived(string id, bool archived)
    {
        using var connection = Open(); using var transaction = connection.BeginTransaction(); using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "UPDATE conversations SET archived=$archived WHERE id=$id AND NOT EXISTS (SELECT 1 FROM scheduled_messages WHERE conversation_id=$id AND status='dispatching')";
        command.Parameters.AddWithValue("$id", id); command.Parameters.AddWithValue("$archived", archived ? 1 : 0);
        if (command.ExecuteNonQuery() != 1) throw new ApiException(409, "Wait for the scheduled message delivery to finish before archiving.");
        if (archived)
        {
            command.CommandText = "UPDATE scheduled_messages SET status='canceled',error='Conversation archived.' WHERE conversation_id=$id AND status='pending'";
            command.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    private static string Hash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    public bool IsDeviceActive(string id) => ActiveDeviceIds().Contains(id, StringComparer.Ordinal);

    public ScheduledMessage Schedule(string conversationId, string text, DateTimeOffset dueAt, string reason, string? deviceId, string? id = null)
    {
        var conversation = Conversations(true).FirstOrDefault(row => row.Id == conversationId) ?? throw new ApiException(404, "Conversation not found.");
        if (conversation.Archived) throw new ApiException(409, "Restore this conversation before scheduling a message.");
        if (string.IsNullOrWhiteSpace(text) || text.Length > 32000) throw new ApiException(400, "Scheduled messages must contain 1–32,000 characters.");
        if (dueAt < DateTimeOffset.UtcNow.AddMinutes(-1) || dueAt > DateTimeOffset.UtcNow.AddYears(1)) throw new ApiException(400, "Choose a time within the next year.");
        id ??= Guid.NewGuid().ToString("N");
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT OR IGNORE INTO scheduled_messages SELECT $id,$conversation,$text,$due,$due,'pending',$reason,NULL,$device WHERE EXISTS (SELECT 1 FROM conversations WHERE id=$conversation AND archived=0)";
        command.Parameters.AddWithValue("$id", id); command.Parameters.AddWithValue("$conversation", conversationId);
        command.Parameters.AddWithValue("$text", text); command.Parameters.AddWithValue("$due", dueAt.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("$reason", reason); command.Parameters.AddWithValue("$device", (object?)deviceId ?? DBNull.Value);
        command.ExecuteNonQuery();
        return Scheduled(conversationId).FirstOrDefault(row => row.Id == id) ?? throw new ApiException(409, "The conversation was archived before the message was scheduled.");
    }

    public ScheduledMessage[] Scheduled(string? conversationId = null)
    {
        using var connection = Open(); using var command = connection.CreateCommand();
        command.CommandText = "SELECT s.id,s.conversation_id,s.text,s.due_at,s.next_at,s.status,s.reason,s.error,s.device_id FROM scheduled_messages s JOIN conversations c ON c.id=s.conversation_id WHERE ($conversation IS NULL OR c.id=$conversation) ORDER BY s.due_at,s.id";
        command.Parameters.AddWithValue("$conversation", (object?)conversationId ?? DBNull.Value);
        using var reader = command.ExecuteReader(); var rows = new List<ScheduledMessage>();
        while (reader.Read()) rows.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2), DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(3)), DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(4)), reader.GetString(5), reader.GetString(6), reader.IsDBNull(7) ? null : reader.GetString(7), reader.IsDBNull(8) ? null : reader.GetString(8)));
        return rows.ToArray();
    }

    public bool UpdateSchedule(string id, string status, string? error = null, DateTimeOffset? next = null, string? expectedStatus = null)
    {
        using var connection = Open(); using var command = connection.CreateCommand();
        command.CommandText = "UPDATE scheduled_messages SET status=$status,error=$error,next_at=COALESCE($next,next_at) WHERE id=$id AND ($expected IS NULL OR status=$expected) AND conversation_id IN (SELECT id FROM conversations WHERE $status NOT IN ('dispatching','pending') OR archived=0)";
        command.Parameters.AddWithValue("$id", id); command.Parameters.AddWithValue("$status", status); command.Parameters.AddWithValue("$error", (object?)error ?? DBNull.Value);
        command.Parameters.AddWithValue("$next", (object?)next?.ToUnixTimeMilliseconds() ?? DBNull.Value); command.Parameters.AddWithValue("$expected", (object?)expectedStatus ?? DBNull.Value);
        return command.ExecuteNonQuery() == 1;
    }

    public DateTimeOffset? RememberTimestamp(string conversationId, string itemId, DateTimeOffset? candidate)
    {
        using var connection = Open(); using var command = connection.CreateCommand();
        command.CommandText = "INSERT OR IGNORE INTO message_times SELECT $conversation,$item,$time WHERE $time IS NOT NULL; SELECT timestamp FROM message_times WHERE conversation_id=$conversation AND item_id=$item";
        command.Parameters.AddWithValue("$conversation", conversationId); command.Parameters.AddWithValue("$item", itemId); command.Parameters.AddWithValue("$time", (object?)candidate?.ToUnixTimeMilliseconds() ?? DBNull.Value);
        var result = command.ExecuteScalar(); return result is long time ? DateTimeOffset.FromUnixTimeMilliseconds(time) : null;
    }

    public void RememberMessageTimestamp(string conversationId, DateTimeOffset? candidate)
    {
        if (candidate is null) return;
        using var connection = Open(); using var command = connection.CreateCommand();
        command.CommandText = "UPDATE conversations SET last_message_at=CASE WHEN last_message_at IS NULL OR last_message_at<$time THEN $time ELSE last_message_at END WHERE id=$conversation";
        command.Parameters.AddWithValue("$conversation", conversationId); command.Parameters.AddWithValue("$time", candidate.Value.ToUnixTimeMilliseconds());
        command.ExecuteNonQuery();
    }

    public (DateTimeOffset? Start, DateTimeOffset? End, long? Duration) RememberTurn(string conversationId, string turnId, DateTimeOffset? start, DateTimeOffset? end, long? duration)
    {
        using var connection = Open(); using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO turn_times VALUES ($conversation,$turn,$start,$end,$duration) ON CONFLICT(conversation_id,turn_id) DO UPDATE SET started=COALESCE(started,$start),completed=COALESCE($end,completed),duration=COALESCE($duration,duration); SELECT started,completed,duration FROM turn_times WHERE conversation_id=$conversation AND turn_id=$turn";
        command.Parameters.AddWithValue("$conversation", conversationId); command.Parameters.AddWithValue("$turn", turnId);
        command.Parameters.AddWithValue("$start", (object?)start?.ToUnixTimeMilliseconds() ?? DBNull.Value); command.Parameters.AddWithValue("$end", (object?)end?.ToUnixTimeMilliseconds() ?? DBNull.Value); command.Parameters.AddWithValue("$duration", (object?)duration ?? DBNull.Value);
        using var reader = command.ExecuteReader(); reader.Read();
        return (reader.IsDBNull(0) ? null : DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(0)), reader.IsDBNull(1) ? null : DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(1)), reader.IsDBNull(2) ? null : reader.GetInt64(2));
    }
}
