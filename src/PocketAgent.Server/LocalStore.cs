using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;

namespace PocketAgent;

public sealed record Device(string Id, string Name, DateTimeOffset ExpiresAt);
public sealed record ConversationReference(string Id, string Title, DateTimeOffset CreatedAt);
public sealed record ScheduledMessage(string Id, string ConversationId, string Text, DateTimeOffset DueAt, DateTimeOffset NextAttemptAt,
    string Status, string Reason, string? Error, [property: System.Text.Json.Serialization.JsonIgnore] string? DeviceId);

public sealed class LocalStore
{
    private readonly string connectionString;
    private readonly string repository;

    public LocalStore(HostSettings settings)
    {
        repository = settings.RepositoryPath;
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
            CREATE TABLE IF NOT EXISTS message_times (conversation_id TEXT NOT NULL, item_id TEXT NOT NULL, timestamp INTEGER NOT NULL, PRIMARY KEY(conversation_id,item_id));
            CREATE TABLE IF NOT EXISTS turn_times (conversation_id TEXT NOT NULL, turn_id TEXT NOT NULL, started INTEGER, completed INTEGER, duration INTEGER, PRIMARY KEY(conversation_id,turn_id));
            """;
        command.ExecuteNonQuery();
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(connectionString);
        connection.Open();
        return connection;
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
        command.CommandText = "UPDATE conversations SET title=$title WHERE id=$id AND repository=$repository";
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$title", title);
        command.Parameters.AddWithValue("$repository", repository);
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

    public void SaveConversation(string id, string title)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO conversations VALUES ($id, $title, $created, $repository)";
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$title", title);
        command.Parameters.AddWithValue("$created", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        command.Parameters.AddWithValue("$repository", repository);
        command.ExecuteNonQuery();
    }

    public ConversationReference[] Conversations()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, title, created_at FROM conversations WHERE repository=$repository ORDER BY created_at DESC, id";
        command.Parameters.AddWithValue("$repository", repository);
        using var reader = command.ExecuteReader();
        var rows = new List<ConversationReference>();
        while (reader.Read())
            rows.Add(new(reader.GetString(0), reader.GetString(1), DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(2))));
        return [.. rows];
    }

    private static string Hash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    public bool IsDeviceActive(string id) => ActiveDeviceIds().Contains(id, StringComparer.Ordinal);

    public ScheduledMessage Schedule(string conversationId, string text, DateTimeOffset dueAt, string reason, string? deviceId, string? id = null)
    {
        if (!Conversations().Any(row => row.Id == conversationId)) throw new ApiException(404, "Conversation not found.");
        if (string.IsNullOrWhiteSpace(text) || text.Length > 32000) throw new ApiException(400, "Scheduled messages must contain 1–32,000 characters.");
        if (dueAt < DateTimeOffset.UtcNow.AddMinutes(-1) || dueAt > DateTimeOffset.UtcNow.AddYears(1)) throw new ApiException(400, "Choose a time within the next year.");
        id ??= Guid.NewGuid().ToString("N");
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT OR IGNORE INTO scheduled_messages VALUES ($id,$conversation,$text,$due,$due,'pending',$reason,NULL,$device)";
        command.Parameters.AddWithValue("$id", id); command.Parameters.AddWithValue("$conversation", conversationId);
        command.Parameters.AddWithValue("$text", text); command.Parameters.AddWithValue("$due", dueAt.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("$reason", reason); command.Parameters.AddWithValue("$device", (object?)deviceId ?? DBNull.Value);
        command.ExecuteNonQuery();
        return Scheduled(conversationId).First(row => row.Id == id);
    }

    public ScheduledMessage[] Scheduled(string? conversationId = null)
    {
        using var connection = Open(); using var command = connection.CreateCommand();
        command.CommandText = "SELECT s.id,s.conversation_id,s.text,s.due_at,s.next_at,s.status,s.reason,s.error,s.device_id FROM scheduled_messages s JOIN conversations c ON c.id=s.conversation_id WHERE c.repository=$repository AND ($conversation IS NULL OR c.id=$conversation) ORDER BY s.due_at,s.id";
        command.Parameters.AddWithValue("$repository", repository); command.Parameters.AddWithValue("$conversation", (object?)conversationId ?? DBNull.Value);
        using var reader = command.ExecuteReader(); var rows = new List<ScheduledMessage>();
        while (reader.Read()) rows.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2), DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(3)), DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(4)), reader.GetString(5), reader.GetString(6), reader.IsDBNull(7) ? null : reader.GetString(7), reader.IsDBNull(8) ? null : reader.GetString(8)));
        return rows.ToArray();
    }

    public bool UpdateSchedule(string id, string status, string? error = null, DateTimeOffset? next = null, string? expectedStatus = null)
    {
        using var connection = Open(); using var command = connection.CreateCommand();
        command.CommandText = "UPDATE scheduled_messages SET status=$status,error=$error,next_at=COALESCE($next,next_at) WHERE id=$id AND ($expected IS NULL OR status=$expected) AND conversation_id IN (SELECT id FROM conversations WHERE repository=$repository)";
        command.Parameters.AddWithValue("$id", id); command.Parameters.AddWithValue("$status", status); command.Parameters.AddWithValue("$error", (object?)error ?? DBNull.Value);
        command.Parameters.AddWithValue("$next", (object?)next?.ToUnixTimeMilliseconds() ?? DBNull.Value); command.Parameters.AddWithValue("$expected", (object?)expectedStatus ?? DBNull.Value); command.Parameters.AddWithValue("$repository", repository);
        return command.ExecuteNonQuery() == 1;
    }

    public DateTimeOffset? RememberTimestamp(string conversationId, string itemId, DateTimeOffset? candidate)
    {
        using var connection = Open(); using var command = connection.CreateCommand();
        command.CommandText = "INSERT OR IGNORE INTO message_times SELECT $conversation,$item,$time WHERE $time IS NOT NULL; SELECT timestamp FROM message_times WHERE conversation_id=$conversation AND item_id=$item";
        command.Parameters.AddWithValue("$conversation", conversationId); command.Parameters.AddWithValue("$item", itemId); command.Parameters.AddWithValue("$time", (object?)candidate?.ToUnixTimeMilliseconds() ?? DBNull.Value);
        var result = command.ExecuteScalar(); return result is long time ? DateTimeOffset.FromUnixTimeMilliseconds(time) : null;
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
