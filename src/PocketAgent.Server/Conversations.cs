using System.Text.Json;
using System.Text.RegularExpressions;

namespace PocketAgent;

public sealed record DisplayImage(string Id, string Source, string AltText, string? MimeType = null);
public sealed record DisplayItem(string Id, string Type, string Text, string Detail, string Status, DateTimeOffset? Timestamp = null, string? ClientMessageId = null, DisplayImage[]? Images = null);
public sealed record PendingRequest(string Id, string Method, JsonElement Parameters, string[] Decisions, bool Supported);
public sealed record ConversationSummary(string Id, string Title, DateTimeOffset CreatedAt, string Status, string Repository, string WorkingDirectory, string HostName, string? Model, bool Archived, string? ReasoningEffort = null, bool Pinned = false, DateTimeOffset? LastMessageAt = null);
public sealed record ConversationSnapshot(string Id, string Title, string Status, string? ActiveTurnId, DisplayItem[] Items, PendingRequest[] Requests, string? Error, string Repository, string WorkingDirectory, string HostName, string PermissionMode, string? SandboxMode, string? ApprovalPolicy, DateTimeOffset? ActiveTurnStartedAt, long? LastWorkDurationMs, string? Model, bool Archived, string? ReasoningEffort = null, bool HistoryTruncated = false, string? HistoryBefore = null, bool Pinned = false, DateTimeOffset? LastMessageAt = null)
{
    public bool SupportsImageAttachments => true;
}
public sealed record ConversationHistoryPage(DisplayItem[] Items, bool HasMore, string? Before);
public sealed record AnswerRequest(string? Decision, Dictionary<string, string[]>? Answers);

public sealed class Conversations(CodexConnection codex, LocalStore store, ProgressPublisher publisher, HostSettings settings, ProviderUsage usage, WorkspaceDirectories directories, ProviderDiagnostics diagnostics, ILogger<Conversations> logger) : BackgroundService
{
    private const int MaxSnapshotItems = 200;
    private const int MaxSnapshotText = 8_000;
    private const int MaxSnapshotDetail = 16_000;
    private const int DefaultHistoryPageSize = 50;
    private const int MaxHistoryPageSize = 100;
    private static readonly TimeSpan InactiveConversationAge = TimeSpan.FromDays(7);
    private static readonly TimeSpan HeartbeatDelay = TimeSpan.FromMinutes(5);
    private const string HeartbeatPrompt = "update requested";
    private sealed class State(ConversationReference reference)
    {
        public ConversationReference Reference { get; set; } = reference;
        public string Status { get; set; } = "idle";
        public string? ActiveTurnId { get; set; }
        public string? Error { get; set; }
        public bool Resumed { get; set; }
        public string? EffectiveModel { get; set; }
        public string? EffectiveEffort { get; set; }
        public string? SandboxMode { get; set; }
        public string? ApprovalPolicy { get; set; }
        public DateTimeOffset? ActiveTurnStartedAt { get; set; }
        public long? LastWorkDurationMs { get; set; }
        public bool Hydrating { get; set; }
        public bool HasWork { get; set; }
        public string? ActivePrompt { get; set; }
        public string? SourceDeviceId { get; set; }
        public string? ScheduledMessageId { get; set; }
        public Queue<(string Id, string Text, bool HasImage)> ClientMessages { get; } = [];
        public Queue<string> HiddenPrompts { get; } = [];
        public DateTimeOffset? HeartbeatDueAt { get; set; }
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public List<DisplayItem> Items { get; } = [];
        public Dictionary<string, (JsonElement RpcId, PendingRequest Request)> Requests { get; } = [];
    }

    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly object turnAdmission = new();
    private int turnsStarting;
    private bool restarting;
    private readonly Dictionary<string, State> states = [];
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, Task<State>> readHydrations = [];
    private long lastPublished;

    public static bool IsActiveStatus(string status) => status is "running" or "awaiting-input" or "starting" or "uncertain";

    public async Task<bool> TryPrepareIdleRestart()
    {
        if (!await gate.WaitAsync(0)) return false;
        try
        {
            lock (turnAdmission)
            {
                if (restarting || turnsStarting != 0 || states.Values.Any(state => IsActiveStatus(state.Status))) return false;
                restarting = true;
                return true;
            }
        }
        finally { gate.Release(); }
    }

    private void RequireAcceptingWork()
    {
        if (restarting) throw new ApiException(409, "PocketAgent is restarting to use the updated Codex version. Try again when the server is back.");
    }

    public async Task<ConversationSummary[]> List(bool archived = false, string? deviceId = null)
    {
        await gate.WaitAsync();
        try
        {
            return store.Conversations(true, deviceId).Where(reference => reference.Archived == archived).Select(reference => new ConversationSummary(reference.Id, reference.Title, reference.CreatedAt,
                states.TryGetValue(reference.Id, out var state) ? state.Status : "saved", Path.GetFileName(reference.WorkingDirectory), reference.WorkingDirectory, Environment.MachineName, reference.Model, reference.Archived, reference.ReasoningEffort, reference.Pinned, reference.LastMessageAt)).ToArray();
        }
        finally { gate.Release(); }
    }

    public async Task<ConversationSnapshot> Create(string title, string? workingDirectory = null, string? model = null, string? reasoningEffort = null, string? deviceId = null)
    {
        if (title.Length > 100)
            throw new ApiException(400, "Conversation names can contain at most 100 characters.");
        var directory = directories.Resolve(workingDirectory);
        if (model is not null) await ValidateModel(model);
        if (reasoningEffort is not null) await ValidateReasoning(model, reasoningEffort);
        await gate.WaitAsync();
        try
        {
            RequireAcceptingWork();
            if (string.IsNullOrWhiteSpace(title))
            {
                var baseName = Path.GetFileName(Path.TrimEndingDirectorySeparator(directory));
                if (baseName.Length == 0) baseName = "New conversation";
                baseName = baseName[..Math.Min(baseName.Length, 85)];
                var existing = store.Conversations().Select(row => row.Title).ToHashSet(StringComparer.OrdinalIgnoreCase);
                title = baseName;
                for (var suffix = 2; existing.Contains(title); suffix++) title = $"{baseName} · {suffix}";
            }
            title = title.Trim();
            var (id, result) = await StartNamedThread(title, directory, model);
            store.SaveConversation(id, title, directory, model, reasoningEffort, deviceId);
            var state = new State(new(id, title, DateTimeOffset.UtcNow, directory, model, false, reasoningEffort)) { Resumed = true };
            CapturePermissions(state, result);
            await ResolveModelSelection(state, result);
            states[id] = state;
            return Snapshot(state);
        }
        finally { gate.Release(); await publisher.Changed(); }
    }

    private async Task<(string Id, JsonElement Response)> StartNamedThread(string title, string directory, string? model)
    {
        await codex.EnsureStarted();
        // Model selection belongs to this conversation; reviewer and permission selection remain desktop-owned.
        var parameters = CodexPermissions.Apply(settings, new { cwd = directory });
        if (model is not null) parameters["model"] = model;
        var response = await codex.Request("thread/start", parameters);
        var id = response.Field("thread").Text("id");
        if (id.Length == 0)
            throw new ApiException(502, "Codex returned a thread without an ID.");
        // Naming writes the empty thread's metadata so Codex can reopen it even before its first turn.
        await codex.Request("thread/name/set", new { threadId = id, name = title });
        return (id, response);
    }

    public async Task<ConversationSnapshot> Reset(string id, string? deviceId = null)
    {
        RequireAccess(id, deviceId);
        var oldState = await GetState(id).ConfigureAwait(false);
        string? newId = null;
        await gate.WaitAsync();
        try
        {
            await oldState.Gate.WaitAsync();
            try
            {
                RequireUnarchived(oldState);
                RequireIdle(oldState);
                RequireAcceptingWork();
                var (replacementId, result) = await StartNamedThread(
                    oldState.Reference.Title, oldState.Reference.WorkingDirectory, oldState.Reference.Model);

                store.ResetConversation(id, replacementId, oldState.Reference);
                newId = replacementId;
                oldState.Reference = oldState.Reference with { Archived = true };
                var newState = new State(new(replacementId, oldState.Reference.Title, DateTimeOffset.UtcNow, oldState.Reference.WorkingDirectory,
                    oldState.Reference.Model, false, oldState.Reference.ReasoningEffort, oldState.Reference.Pinned)) { Resumed = true };
                CapturePermissions(newState, result);
                await ResolveModelSelection(newState, result);
                states[replacementId] = newState;
                return Snapshot(newState);
            }
            finally { oldState.Gate.Release(); }
        }
        finally
        {
            gate.Release();
            await publisher.Changed(id);
            if (newId is not null) await publisher.Changed(newId);
        }
    }

    public async Task<ConversationSnapshot> Get(string id, string? deviceId = null)
    {
        RequireAccess(id, deviceId);
        try
        {
            var hydrated = await GetState(id).ConfigureAwait(false);
            await hydrated.Gate.WaitAsync();
            try { return Snapshot(hydrated); }
            finally { hydrated.Gate.Release(); }
        }
        catch (ApiException exception) when (IsMissingThread(exception))
        {
            var reference = store.Conversations(true).FirstOrDefault(row => row.Id == id)
                ?? throw new ApiException(404, "Conversation not found on this PocketAgent host.");
            logger.LogWarning("Conversation {ConversationId} has local metadata but its Codex thread is unavailable.", id);
            return UnavailableSnapshot(reference, exception.Message);
        }
    }

    public async Task<ConversationHistoryPage> History(string id, string? before, int limit, string? deviceId = null)
    {
        RequireAccess(id, deviceId);
        var state = await GetState(id).ConfigureAwait(false);
        await state.Gate.WaitAsync();
        try
        {
            var pageSize = Math.Clamp(limit <= 0 ? DefaultHistoryPageSize : limit, 1, MaxHistoryPageSize);
            var end = state.Items.Count;
            if (!string.IsNullOrWhiteSpace(before))
            {
                end = state.Items.FindIndex(item => item.Id == before);
                if (end < 0) throw new ApiException(400, "That history cursor is no longer valid. Refresh the conversation.");
            }
            var start = Math.Max(0, end - pageSize);
            var items = state.Items.Skip(start).Take(end - start).Select(Bounded).ToArray();
            return new(items, start > 0, start > 0 ? state.Items[start].Id : null);
        }
        finally { state.Gate.Release(); }
    }

    public async Task<(byte[] Bytes, string ContentType)> ReadImage(string id, string source, string? deviceId = null)
    {
        RequireAccess(id, deviceId);
        var state = await GetState(id).ConfigureAwait(false);
        await state.Gate.WaitAsync().ConfigureAwait(false);
        string workingDirectory;
        try
        {
            workingDirectory = state.Reference.WorkingDirectory;
            if (source.StartsWith(MessageImage.ReferencePrefix, StringComparison.Ordinal))
            {
                var imageId = source[MessageImage.ReferencePrefix.Length..];
                var image = state.Items.Where(item => item.Type == "userMessage")
                    .SelectMany(item => item.Images ?? [])
                    .FirstOrDefault(image => image.Id == imageId && IsEmbeddedImage(image));
                source = image?.Source ?? throw new ApiException(404, "That attachment is no longer available.");
            }
        }
        finally { state.Gate.Release(); }

        if (source.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            var comma = source.IndexOf(',');
            if (comma <= 5 || comma == source.Length - 1) throw new ApiException(400, "That image data is invalid.");
            var header = source[..comma];
            var payload = source[(comma + 1)..];
            if (!header.Contains(";base64", StringComparison.OrdinalIgnoreCase)) throw new ApiException(400, "That image data is not base64 encoded.");
            try { return (Convert.FromBase64String(payload), header[5..header.IndexOf(';')]); }
            catch (FormatException) { throw new ApiException(400, "That image data is invalid."); }
        }
        if (Uri.TryCreate(source, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
            throw new ApiException(400, "Remote image URLs are not proxied by PocketAgent.");
        var candidate = Uri.UnescapeDataString(source);
        var fullPath = Path.GetFullPath(Path.IsPathRooted(candidate) ? candidate : Path.Combine(workingDirectory, candidate));
        var root = Path.GetFullPath(workingDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new ApiException(403, "Images must be inside the conversation working directory.");
        if (!File.Exists(fullPath)) throw new ApiException(404, "The image is no longer available on the desktop.");
        var info = new FileInfo(fullPath);
        if (info.Length > 16 * 1024 * 1024) throw new ApiException(413, "That image is too large to view.");
        return (await File.ReadAllBytesAsync(fullPath).ConfigureAwait(false), ContentType(fullPath));
    }

    private async Task<State> GetState(string id)
    {
        await gate.WaitAsync();
        try
        {
            if (states.TryGetValue(id, out var existing)) return existing;
        }
        finally { gate.Release(); }
        return await readHydrations.GetOrAdd(id, HydrateForRead).ConfigureAwait(false);
    }

    public async Task<string[]> ActiveConversationIds()
    {
        await gate.WaitAsync();
        try { return states.Values.Where(state => state.ActiveTurnId is not null).Select(state => state.Reference.Id).ToArray(); }
        finally { gate.Release(); }
    }

    public async Task TryHeartbeat(string id)
    {
        if (!settings.EnableHeartbeats) return;
        var state = await GetState(id).ConfigureAwait(false);
        await state.Gate.WaitAsync();
        try
        {
            if (state.ActiveTurnId is null || state.Status is not ("running" or "awaiting-input") || state.Requests.Count > 0)
                return;
            var now = DateTimeOffset.UtcNow;
            if (state.HeartbeatDueAt is null || state.HeartbeatDueAt > now) return;
            var turnId = state.ActiveTurnId;
            state.HeartbeatDueAt = now + HeartbeatDelay;
            state.HiddenPrompts.Enqueue(HeartbeatPrompt);
            ServerDiagnostics.Log("heartbeat", Microsoft.Extensions.Logging.LogLevel.Information,
                $"steer_start conversation_id={id} turn_id={turnId} queued_hidden_prompts={state.HiddenPrompts.Count}");
            try
            {
                var result = await codex.Request("turn/steer", new
                {
                    threadId = state.Reference.Id,
                    expectedTurnId = turnId,
                    input = new[] { new { type = "text", text = HeartbeatPrompt, text_elements = Array.Empty<object>() } }
                });
                if (result.Text("turnId") != turnId)
                    throw new ApiException(504, "Codex did not confirm the heartbeat steer.");
                ServerDiagnostics.Log("heartbeat", Microsoft.Extensions.Logging.LogLevel.Information,
                    $"steer_ok conversation_id={id} turn_id={turnId}");
            }
            catch (Exception exception)
            {
                if (state.HiddenPrompts.TryPeek(out var hidden) && hidden == HeartbeatPrompt) state.HiddenPrompts.Dequeue();
                state.HeartbeatDueAt = now + TimeSpan.FromMinutes(1);
                logger.LogWarning(exception, "Heartbeat steer failed for conversation {ConversationId}.", id);
                ServerDiagnostics.Log("heartbeat", Microsoft.Extensions.Logging.LogLevel.Warning,
                    $"steer_failed conversation_id={id} turn_id={turnId}", exception);
            }
        }
        finally { state.Gate.Release(); }
    }

    public async Task<ConversationSnapshot> Rename(string id, string title, string? deviceId = null)
    {
        RequireAccess(id, deviceId);
        if (string.IsNullOrWhiteSpace(title) || title.Length > 100)
            throw new ApiException(400, "Use a name of 1–100 characters.");
        var state = await GetState(id);
        await state.Gate.WaitAsync();
        try
        {
            RequireUnarchived(state);
            store.RenameConversation(id, title.Trim());
            state.Reference = state.Reference with { Title = title.Trim() };
            return Snapshot(state);
        }
        finally { state.Gate.Release(); await publisher.Changed(id); }
    }

    private async Task<ModelOption> ValidateModel(string model)
    {
        var option = (await diagnostics.Models()).FirstOrDefault(option => option.Model == model);
        if (model.Length is 0 or > 200 || option is null)
            throw new ApiException(400, "Choose a model from this server's available models.");
        return option;
    }

    private async Task ValidateReasoning(string? model, string effort)
    {
        if (model is null) throw new ApiException(400, "Choose a model before selecting reasoning effort.");
        var option = await ValidateModel(model);
        if (!option.SupportedReasoningEfforts.Contains(effort, StringComparer.OrdinalIgnoreCase))
            throw new ApiException(400, "Choose a reasoning effort supported by the selected model.");
    }

    private static void RequireUnarchived(State state)
    {
        if (state.Reference.Archived) throw new ApiException(409, "Restore this conversation before changing it or sending messages.");
    }

    public void RequireAccess(string id, string? deviceId)
    {
        if (deviceId is null) return;
        if (!store.HasConversationAccess(id, deviceId)) throw new ApiException(404, "Conversation not found.");
    }

    private static void RequireIdle(State state)
    {
        if (state.ActiveTurnId is not null || state.Status is "starting" or "uncertain" or "disconnected" || state.Requests.Count > 0)
            throw new ApiException(409, "Finish or stop active work and confirm its outcome before changing this conversation.");
    }

    public async Task<ConversationSnapshot> SetModel(string id, string model, string? deviceId = null)
    {
        RequireAccess(id, deviceId);
        var state = await GetState(id);
        await state.Gate.WaitAsync();
        try
        {
            RequireUnarchived(state); RequireIdle(state);
            var option = await ValidateModel(model);
            var previous = state.Reference.ReasoningEffort;
            var effort = previous is not null && option.SupportedReasoningEfforts.Contains(previous, StringComparer.OrdinalIgnoreCase) ? previous : null;
            store.SetModelSelection(id, model, effort);
            state.Reference = state.Reference with { Model = model, ReasoningEffort = effort };
            await ResolveModelSelection(state, default);
            return Snapshot(state);
        }
        finally { state.Gate.Release(); await publisher.Changed(id); }
    }

    public async Task<ConversationSnapshot> SetModelSelection(string id, string model, string? effort, string? deviceId = null)
    {
        RequireAccess(id, deviceId);
        var state = await GetState(id);
        await state.Gate.WaitAsync();
        try
        {
            RequireUnarchived(state);
            RequireIdle(state);
            var option = await ValidateModel(model);
            if (effort is not null && !string.Equals(effort, NonEmpty(option.DefaultReasoningEffort), StringComparison.OrdinalIgnoreCase) &&
                !option.SupportedReasoningEfforts.Contains(effort, StringComparer.OrdinalIgnoreCase))
                throw new ApiException(400, "Choose a reasoning effort supported by the selected model.");
            store.SetModelSelection(id, model, effort);
            state.Reference = state.Reference with { Model = model, ReasoningEffort = effort };
            state.EffectiveModel = model;
            state.EffectiveEffort = NonEmpty(option.DefaultReasoningEffort);
            return Snapshot(state);
        }
        finally { state.Gate.Release(); await publisher.Changed(id); }
    }

    public async Task<ConversationSnapshot> SetReasoning(string id, string? effort, string? deviceId = null)
    {
        RequireAccess(id, deviceId);
        var state = await GetState(id);
        await state.Gate.WaitAsync();
        try
        {
            RequireUnarchived(state); RequireIdle(state);
            if (effort is not null) await ValidateReasoning(state.Reference.Model, effort);
            store.SetReasoningEffort(id, effort);
            state.Reference = state.Reference with { ReasoningEffort = effort };
            await ResolveModelSelection(state, default);
            return Snapshot(state);
        }
        finally { state.Gate.Release(); await publisher.Changed(id); }
    }

    public async Task<ConversationSnapshot> Archive(string id, bool archived, string? deviceId = null)
    {
        RequireAccess(id, deviceId);
        try
        {
            try
            {
                var state = await GetState(id);
                await state.Gate.WaitAsync();
                try
                {
                    RequireIdle(state);
                    store.SetArchived(id, archived);
                    state.Reference = state.Reference with { Archived = archived };
                    return Snapshot(state);
                }
                finally { state.Gate.Release(); }
            }
            catch (ApiException exception) when (IsMissingThread(exception))
            {
                var reference = store.Conversations(true).FirstOrDefault(row => row.Id == id)
                    ?? throw new ApiException(404, "Conversation not found on this PocketAgent host.");
                store.SetArchived(id, archived);
                logger.LogInformation("Archived conversation {ConversationId} using local metadata because its Codex thread is unavailable.", id);
                return UnavailableSnapshot(reference with { Archived = archived }, exception.Message);
            }
        }
        finally { await publisher.Changed(id); }
    }

    public async Task<int> ArchiveInactive(DateTimeOffset now)
    {
        var cutoff = now - InactiveConversationAge;
        await gate.WaitAsync();
        string[] active;
        try
        {
            active = states.Values
                .Where(state => state.ActiveTurnId is not null || state.Status is "starting" or "running" or "awaiting-input" or "uncertain")
                .Select(state => state.Reference.Id)
                .ToArray();
        }
        finally { gate.Release(); }

        var archived = store.ArchiveInactive(cutoff, active);
        foreach (var id in archived)
        {
            await gate.WaitAsync();
            try
            {
                if (states.TryGetValue(id, out var state))
                {
                    await state.Gate.WaitAsync();
                    try { state.Reference = state.Reference with { Archived = true }; }
                    finally { state.Gate.Release(); }
                }
            }
            finally { gate.Release(); }
            await publisher.Changed(id);
            logger.LogInformation("Archived inactive conversation {ConversationId} after seven days without a message.", id);
        }
        return archived.Length;
    }

    public async Task<ConversationSnapshot> SetPinned(string id, bool pinned, string? deviceId = null)
    {
        RequireAccess(id, deviceId);
        var state = await GetState(id);
        await state.Gate.WaitAsync();
        try
        {
            RequireUnarchived(state);
            store.SetPinned(id, pinned);
            state.Reference = state.Reference with { Pinned = pinned };
            return Snapshot(state);
        }
        finally { state.Gate.Release(); await publisher.Changed(id); }
    }

    public Task StartTurn(string id, string text, string? expectedTurnId = null, string? sourceDeviceId = null, string? scheduledMessageId = null, string? clientMessageId = null) =>
        StartTurnWithInput(id, text, expectedTurnId, sourceDeviceId, scheduledMessageId, clientMessageId);

    internal async Task StartTurnWithInput(string id, string text, string? expectedTurnId, string? sourceDeviceId, string? scheduledMessageId = null, string? clientMessageId = null, string? imageUrl = null)
    {
        lock (turnAdmission)
        {
            RequireAcceptingWork();
            turnsStarting++;
        }
        try { await StartTurnCore(id, text, expectedTurnId, sourceDeviceId, scheduledMessageId, clientMessageId, imageUrl); }
        finally { lock (turnAdmission) turnsStarting--; }
    }

    private async Task StartTurnCore(string id, string text, string? expectedTurnId, string? sourceDeviceId, string? scheduledMessageId, string? clientMessageId, string? imageUrl)
    {
        RequireAccess(id, sourceDeviceId);
        if ((imageUrl is null && string.IsNullOrWhiteSpace(text)) || text.Length > 32_000)
            throw new ApiException(400, "Messages must contain 1–32,000 characters.");
        var state = await GetState(id);
        if (imageUrl is not null && string.IsNullOrWhiteSpace(text)) text = "";
        var input = new List<object>(2);
        if (text.Length > 0) input.Add(new { type = "text", text, text_elements = Array.Empty<object>() });
        if (imageUrl is not null) input.Add(new { type = "image", url = imageUrl });
        await state.Gate.WaitAsync();
        try
        {
            RequireUnarchived(state);
            if (expectedTurnId is not null)
            {
                if (expectedTurnId.Length == 0 || state.ActiveTurnId != expectedTurnId || state.Status is "starting" or "uncertain" or "disconnected")
                    throw new ApiException(409, "The expected turn is no longer available. Your steering prompt was not sent. Refresh and submit explicitly.");
                try
                {
                    var result = await codex.Request("turn/steer", new
                    {
                        threadId = id,
                        expectedTurnId,
                        input
                    });
                    if (result.Text("turnId") != expectedTurnId)
                        throw new ApiException(504, "Codex did not confirm the expected turn. Steering delivery is unknown; do not resend automatically.");
                    if (!string.IsNullOrWhiteSpace(clientMessageId)) state.ClientMessages.Enqueue((clientMessageId, text, imageUrl is not null));
                    state.HeartbeatDueAt = DateTimeOffset.UtcNow + HeartbeatDelay;
                }
                catch (ApiException exception) when (exception.Status is 503 or 504)
                {
                    state.Status = "uncertain";
                    state.Error = exception.Message;
                    throw;
                }
                return;
            }
            if (state.ActiveTurnId is not null || state.Status is "starting" or "uncertain" or "disconnected" || state.Requests.Count > 0)
                throw new ApiException(409, "This conversation has active or unconfirmed work. Wait for it to finish or interrupt it.");
            if (!state.Resumed)
            {
                var resumeParameters = CodexPermissions.Apply(settings, new { threadId = id, cwd = directories.Resolve(state.Reference.WorkingDirectory) });
                if (state.Reference.Model is not null) resumeParameters["model"] = state.Reference.Model;
                var resumed = await codex.Request("thread/resume", resumeParameters);
                CapturePermissions(state, resumed);
                await Hydrate(state, resumed.GetProperty("thread"));
                state.Resumed = true;
            }
            state.Status = "starting";
            state.Error = null;
            state.ActivePrompt = imageUrl is null ? text : null;
            state.SourceDeviceId = sourceDeviceId; state.ScheduledMessageId = scheduledMessageId;
            state.HasWork = false; state.ActiveTurnStartedAt = DateTimeOffset.UtcNow;
            try
            {
                var parameters = CodexPermissions.Apply(settings, new
                {
                    threadId = id,
                    cwd = directories.Resolve(state.Reference.WorkingDirectory),
                    input
                }, turn: true, workingDirectory: state.Reference.WorkingDirectory);
                if (state.Reference.Model is not null) parameters["model"] = state.Reference.Model;
                if (state.Reference.ReasoningEffort is not null) parameters["effort"] = state.Reference.ReasoningEffort;
                var result = await codex.Request("turn/start", parameters);
                var turn = result.GetProperty("turn");
                if (turn.Text("id").Length == 0) throw new ApiException(504, "Codex did not confirm a turn ID. Delivery is unknown; do not resend automatically.");
                state.ActiveTurnId = turn.Text("id");
                state.Status = "running";
                state.HeartbeatDueAt = DateTimeOffset.UtcNow + HeartbeatDelay;
                if (!string.IsNullOrWhiteSpace(clientMessageId)) state.ClientMessages.Enqueue((clientMessageId, text, imageUrl is not null));
                AddTurn(state, turn);
            }
            catch (ApiException exception)
            {
                state.Status = exception.Status is 504 or 503 ? "uncertain" : "failed";
                state.Error = exception.Message;
                throw;
            }
        }
        finally { state.Gate.Release(); await publisher.Changed(id); }
    }

    public async Task Interrupt(string id, string? deviceId = null)
    {
        RequireAccess(id, deviceId);
        var state = await GetState(id);
        await state.Gate.WaitAsync();
        try
        {
            if (state.ActiveTurnId is null)
                throw new ApiException(409, "There is no confirmed active turn to interrupt.");
            await codex.Request("turn/interrupt", new { threadId = id, turnId = state.ActiveTurnId });
            // Keep the turn and requests until Codex confirms completion/resolution.
        }
        finally { state.Gate.Release(); await publisher.Changed(id); }
    }

    public async Task Answer(string id, string requestId, AnswerRequest answer, string? deviceId = null)
    {
        RequireAccess(id, deviceId);
        var state = await GetState(id);
        await state.Gate.WaitAsync();
        try
        {
            if (!state.Requests.TryGetValue(requestId, out var entry) || !entry.Request.Supported)
                throw new ApiException(409, "This request was resolved, is stale, or is unsupported.");
            object response;
            if (entry.Request.Method == "item/tool/requestUserInput")
            {
                var questions = entry.Request.Parameters.GetProperty("questions").EnumerateArray().ToArray();
                if (answer.Answers is null || answer.Answers.Count != questions.Length || questions.Any(question =>
                    !answer.Answers.TryGetValue(question.Text("id"), out var values) || values.Length != 1 || string.IsNullOrWhiteSpace(values[0]) || values[0].Length > 8000))
                    throw new ApiException(400, "Provide one nonempty answer for each question.");
                response = new { answers = answer.Answers.ToDictionary(pair => pair.Key, pair => new { answers = pair.Value }) };
            }
            else
            {
                if (answer.Decision is null || !entry.Request.Decisions.Contains(answer.Decision, StringComparer.Ordinal))
                    throw new ApiException(400, "That decision is not available for this request.");
                response = new { decision = answer.Decision };
            }
            await codex.Respond(entry.RpcId, response);
            state.Requests.Remove(requestId);
            state.Status = state.Requests.Count > 0 ? "awaiting-input" : "running";
        }
        finally { state.Gate.Release(); await publisher.Changed(id); }
    }

    private async Task<State> HydrateForRead(string id)
    {
        try
        {
            await gate.WaitAsync();
            ConversationReference reference;
            try
            {
                if (states.TryGetValue(id, out var existing)) return existing;
                reference = store.Conversations(true).FirstOrDefault(row => row.Id == id) ?? throw new ApiException(404, "Conversation not found on this PocketAgent host.");
            }
            finally { gate.Release(); }

            await codex.EnsureStarted();
            var state = new State(reference);
            var result = await codex.Request("thread/read", new { threadId = id, includeTurns = false });
            await Hydrate(state, result.GetProperty("thread"));
            await ResolveModelSelection(state, result);
            await gate.WaitAsync();
            try
            {
                if (states.TryGetValue(id, out var existing)) return existing;
                states[id] = state;
                return state;
            }
            finally { gate.Release(); }
        }
        finally { readHydrations.TryRemove(id, out _); }
    }

    private static bool IsMissingThread(ApiException exception) =>
        exception.Message.Contains("thread not loaded", StringComparison.OrdinalIgnoreCase) ||
        exception.Message.Contains("thread not found", StringComparison.OrdinalIgnoreCase);

    private ConversationSnapshot UnavailableSnapshot(ConversationReference reference, string error) =>
        new(reference.Id, reference.Title, "disconnected", null, [], [], error,
            Path.GetFileName(reference.WorkingDirectory), reference.WorkingDirectory, Environment.MachineName,
            settings.PermissionMode, null, null, null, null, reference.Model, reference.Archived,
            reference.ReasoningEffort, false, null, reference.Pinned, reference.LastMessageAt);

    private async Task Hydrate(State state, JsonElement thread)
    {
        state.Hydrating = true;
        state.Items.Clear();
        if (thread.Text("historyMode") == "paginated")
        {
            string? cursor = null;
            do
            {
                var page = await codex.Request("thread/turns/list", new { threadId = state.Reference.Id, cursor, limit = 100, sortDirection = "asc", itemsView = "full" });
                foreach (var turn in page.GetProperty("data").EnumerateArray())
                    AddTurn(state, turn);
                cursor = page.Text("nextCursor") is { Length: > 0 } value ? value : null;
            } while (cursor is not null);
        }
        else
        {
            var result = await codex.Request("thread/read", new { threadId = state.Reference.Id, includeTurns = true });
            var turns = result.GetProperty("thread").Field("turns");
            if (turns.ValueKind == JsonValueKind.Array)
                foreach (var turn in turns.EnumerateArray())
                    AddTurn(state, turn);
        }
        // A restored process cannot claim a previous execution is still running.
        if (!state.Resumed)
        {
            state.ActiveTurnId = null;
            state.Status = "idle";
        }
        state.Hydrating = false;
    }

    private void AddTurn(State state, JsonElement turn)
    {
        var start = Timestamp(turn.Field("startedAt")) ?? (state.Hydrating ? null : state.ActiveTurnStartedAt);
        var end = Timestamp(turn.Field("completedAt"));
        var completed = turn.Text("status") is "completed" or "failed" or "interrupted";
        end ??= completed && !state.Hydrating ? DateTimeOffset.UtcNow : null;
        long? duration = turn.Field("durationMs").TryGetInt64Safe(out var milliseconds) ? Math.Max(0, milliseconds) :
            start is not null && end is not null ? Math.Max(0, (long)(end - start).Value.TotalMilliseconds) : null;
        var times = store.RememberTurn(state.Reference.Id, turn.Text("id"), start, end, duration);
        if (completed) state.LastWorkDurationMs = times.Duration;
        else state.ActiveTurnStartedAt = times.Start;
        var items = turn.Field("items");
        if (items.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in items.EnumerateArray())
            {
                var rendered = Render(item) with { Timestamp = state.Hydrating ? null : item.Text("type") == "userMessage" ? times.Start : times.End ?? times.Start };
                Upsert(state, rendered);
            }
        }
    }

    private void Upsert(State state, DisplayItem item)
    {
        if (item.Id.Length == 0 || item.Type == "reasoning")
            return;
        if (item.Images is null && string.Equals(item.Text.Trim(), HeartbeatPrompt, StringComparison.OrdinalIgnoreCase))
        {
            var queued = state.HiddenPrompts.Count > 0;
            var hidden = item.Type == "userMessage" && IsHiddenPrompt(state, item.Text);
            ServerDiagnostics.Log("heartbeat", hidden ? Microsoft.Extensions.Logging.LogLevel.Information : Microsoft.Extensions.Logging.LogLevel.Warning,
                $"item_seen conversation_id={state.Reference.Id} item_id={item.Id} item_type={item.Type} hydrating={state.Hydrating} queued={queued} hidden={hidden}");
            if (hidden) return;
        }
        var index = state.Items.FindIndex(existing => existing.Id == item.Id);
        if (index >= 0)
            item = item with { ClientMessageId = state.Items[index].ClientMessageId };
        else if (!state.Hydrating && item.Type == "userMessage" && state.ClientMessages.TryPeek(out var pending) &&
            string.Equals(pending.Text, item.Text, StringComparison.Ordinal) &&
            pending.HasImage == (item.Images?.Any(IsEmbeddedImage) == true))
        {
            state.ClientMessages.Dequeue();
            item = item with { ClientMessageId = pending.Id };
        }
        var existingTime = index >= 0 ? state.Items[index].Timestamp : null;
        item = item with { Timestamp = existingTime ?? store.RememberTimestamp(state.Reference.Id, item.Id, item.Timestamp ?? (state.Hydrating ? null : DateTimeOffset.UtcNow)) };
        if (item.Type is "userMessage" or "agentMessage")
        {
            store.RememberMessageTimestamp(state.Reference.Id, item.Timestamp);
            if (item.Timestamp is { } messageTime && (state.Reference.LastMessageAt is null || messageTime > state.Reference.LastMessageAt))
                state.Reference = state.Reference with { LastMessageAt = messageTime };
        }
        if (!state.Hydrating && item.Type == "agentMessage" && item.Text.Length > 0)
            state.HeartbeatDueAt = DateTimeOffset.UtcNow + HeartbeatDelay;
        if (!state.Hydrating && item.Type != "userMessage" && (item.Type != "agentMessage" || item.Text.Length > 0)) state.HasWork = true;
        if (index < 0)
            state.Items.Add(item);
        else
            state.Items[index] = item;
    }

    private static DisplayItem Render(JsonElement item)
    {
        var type = item.Text("type");
        var text = item.Text("text");
        if (type == "userMessage" && item.Field("content") is { ValueKind: JsonValueKind.Array } content)
            text = string.Join("\n", content.EnumerateArray().Where(part => part.Text("type") == "text").Select(part => part.Text("text")));
        var detail = type switch
        {
            "commandExecution" => item.Text("command") + "\n" + item.Text("aggregatedOutput"),
            "fileChange" => item.Field("changes").ValueKind == JsonValueKind.Array ? JsonSerializer.Serialize(item.GetProperty("changes"), new JsonSerializerOptions { WriteIndented = true }) : "",
            "mcpToolCall" => item.Text("server") + "/" + item.Text("tool"),
            "webSearch" => item.Text("query"),
            _ => ""
        };
        return new(item.Text("id"), type, text, detail, item.Text("status"), Images: ExtractImages(item, text));
    }

    private static DisplayImage[]? ExtractImages(JsonElement item, string text)
    {
        var images = new List<DisplayImage>();
        var type = item.Text("type");
        if (IsImageType(type)) AddStructuredImage(images, item, "image-" + item.Text("id"));
        if (item.Field("content") is { ValueKind: JsonValueKind.Array } content)
            foreach (var part in content.EnumerateArray())
                if (IsImageType(part.Text("type"))) AddStructuredImage(images, part, "image-" + item.Text("id") + "-" + images.Count);
        if (item.Field("result").Field("content") is { ValueKind: JsonValueKind.Array } resultContent)
            foreach (var part in resultContent.EnumerateArray())
                if (IsImageType(part.Text("type"))) AddStructuredImage(images, part, "image-" + item.Text("id") + "-" + images.Count);
        foreach (Match match in Regex.Matches(text, @"!\[([^\]]*)\]\(([^)]+)\)"))
        {
            var source = MarkdownImageSource(match.Groups[2].Value);
            if (source.Length > 0) images.Add(new("markdown-" + images.Count, source, match.Groups[1].Value));
        }
        return images.Count == 0 ? null : images.ToArray();
    }

    private static string MarkdownImageSource(string value)
    {
        var source = value.Trim();
        if (source.StartsWith('<') && source.EndsWith('>')) return source[1..^1].Trim();
        // Keep spaces in ordinary local paths, but ignore an optional markdown title.
        var title = Regex.Match(source, "\\s+[\\\"'].*[\\\"']\\s*$");
        return (title.Success ? source[..title.Index] : source).Trim();
    }

    private static bool IsImageType(string type) => type.Contains("image", StringComparison.OrdinalIgnoreCase) || type.Contains("screenshot", StringComparison.OrdinalIgnoreCase);

    private static void AddStructuredImage(List<DisplayImage> images, JsonElement value, string id)
    {
        var source = ImageSource(value, "path");
        if (source.Length == 0) source = ImageSource(value, "url");
        if (source.Length == 0) source = ImageSource(value, "image_url");
        var mime = ImageSource(value, "mimeType");
        if (source.Length == 0)
        {
            source = ImageSource(value, "data");
            if (source.Length > 0 && mime.StartsWith("image/", StringComparison.OrdinalIgnoreCase) &&
                !source.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                source = $"data:{mime};base64,{source}";
        }
        if (source.Length == 0) return;
        var alt = value.Text("alt");
        if (alt.Length == 0) alt = value.Text("caption");
        images.Add(new(id, source, alt, mime.Length == 0 ? null : mime));
    }

    private static string ImageSource(JsonElement value, string name)
    {
        return ImageSourceValue(value.Field(name));
    }

    private static string ImageSourceValue(JsonElement field)
    {
        if (field.ValueKind == JsonValueKind.String) return field.GetString() ?? "";
        if (field.ValueKind != JsonValueKind.Object) return "";
        foreach (var nestedName in new[] { "url", "uri", "path", "data" })
        {
            var nested = field.Field(nestedName);
            if (ImageSourceValue(nested) is { Length: > 0 } source) return source;
        }
        return "";
    }

    private static string ContentType(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".png" => "image/png", ".jpg" or ".jpeg" => "image/jpeg", ".gif" => "image/gif", ".webp" => "image/webp", ".bmp" => "image/bmp", ".svg" => "image/svg+xml", _ => "application/octet-stream"
    };

    private static bool IsHiddenPrompt(State state, string text)
    {
        if (!string.Equals(text.Trim(), HeartbeatPrompt, StringComparison.OrdinalIgnoreCase)) return false;
        if (state.HiddenPrompts.TryPeek(out var hidden) && string.Equals(hidden, HeartbeatPrompt, StringComparison.OrdinalIgnoreCase))
        {
            state.HiddenPrompts.Dequeue();
            return true;
        }
        return false;
    }

    private ConversationSnapshot Snapshot(State state)
    {
        var first = Math.Max(0, state.Items.Count - MaxSnapshotItems);
        var historyTruncated = first > 0;
        var items = state.Items.Skip(first).Select(Bounded).ToArray();
        return new(state.Reference.Id, state.Reference.Title, state.Status, state.ActiveTurnId,
            items, state.Requests.Values.Select(entry => entry.Request).ToArray(), state.Error, Path.GetFileName(state.Reference.WorkingDirectory), state.Reference.WorkingDirectory, Environment.MachineName,
            settings.PermissionMode, state.SandboxMode, state.ApprovalPolicy, state.ActiveTurnId is null ? null : state.ActiveTurnStartedAt, state.LastWorkDurationMs,
            state.Reference.Model ?? state.EffectiveModel, state.Reference.Archived, state.Reference.ReasoningEffort ?? state.EffectiveEffort,
            historyTruncated, historyTruncated ? state.Items[first].Id : null, state.Reference.Pinned, state.Reference.LastMessageAt);
    }

    private static bool IsEmbeddedImage(DisplayImage image) => image.Source.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase);

    private static DisplayItem Bounded(DisplayItem item) => item with
    {
        Text = Limit(item.Text, MaxSnapshotText),
        Detail = Limit(item.Detail, MaxSnapshotDetail),
        Images = item.Type == "userMessage" ? item.Images?.Select(image => IsEmbeddedImage(image)
            ? image with { Source = MessageImage.ReferencePrefix + image.Id, AltText = image.AltText.Length == 0 ? "Pasted image" : image.AltText }
            : image).ToArray() : item.Images
    };

    private static string Limit(string value, int length) => value.Length <= length ? value : value[..length] + "\n[output truncated]";

    private static DateTimeOffset? Timestamp(JsonElement value) => value.TryGetInt64Safe(out var seconds) && seconds > 0 && seconds < 253402300799 ? DateTimeOffset.FromUnixTimeSeconds(seconds) : null;

    private async Task ResolveModelSelection(State state, JsonElement response)
    {
        var reference = state.Reference;
        state.EffectiveModel = reference.Model ?? NonEmpty(response.Text("model")) ?? NonEmpty(response.Field("thread").Text("model")) ?? state.EffectiveModel;
        state.EffectiveEffort = reference.ReasoningEffort ?? NonEmpty(response.Text("reasoningEffort"));
        if (state.EffectiveModel is not null && state.EffectiveEffort is not null) return;

        try
        {
            var config = (await codex.Request("config/read", new { includeLayers = false, cwd = reference.WorkingDirectory })).Field("config");
            state.EffectiveModel ??= NonEmpty(config.Text("model"));
            state.EffectiveEffort ??= NonEmpty(config.Text("model_reasoning_effort"));
        }
        catch (ApiException exception)
        {
            logger.LogDebug("Could not read Codex model settings for {ConversationId}: {Status}", reference.Id, exception.Status);
        }

        if (state.EffectiveModel is not null && state.EffectiveEffort is not null) return;
        try
        {
            var models = await diagnostics.Models();
            state.EffectiveModel ??= models.FirstOrDefault(option => option.IsDefault)?.Model;
            state.EffectiveEffort ??= NonEmpty(models.FirstOrDefault(option => option.Model == state.EffectiveModel)?.DefaultReasoningEffort);
        }
        catch (ApiException exception)
        {
            logger.LogDebug("Could not resolve model settings for {ConversationId}: {Status}", reference.Id, exception.Status);
        }
    }

    private static string? NonEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    private static void CapturePermissions(State state, JsonElement response)
    {
        state.SandboxMode = response.Field("sandbox").Text("type") is { Length: > 0 } sandbox ? sandbox : null;
        state.ApprovalPolicy = response.Field("approvalPolicy") is { ValueKind: JsonValueKind.String } approval ? approval.GetString() : null;
    }

    private static JsonElement PublicRequestParameters(JsonElement parameters)
    {
        // New protocol fields are private by default, particularly host authentication metadata.
        string[] fields = ["command", "reason", "cwd", "grantRoot", "itemId", "networkApprovalContext", "additionalPermissions", "questions"];
        return JsonSerializer.SerializeToElement(fields.Where(name => parameters.TryGetProperty(name, out _))
            .ToDictionary(name => name, name => parameters.GetProperty(name).Clone()));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var message in codex.Events.ReadAllAsync(stoppingToken))
        {
            var method = message.Text("method");
            if (!message.TryGetProperty("id", out _) && method is not (
                "pocketagent/disconnected" or "turn/started" or "turn/completed" or "item/started" or "item/completed" or "account/rateLimits/updated" or
                "item/agentMessage/delta" or "item/commandExecution/outputDelta" or "serverRequest/resolved" or "error"))
                continue;
            await gate.WaitAsync(stoppingToken);
            string? id = null;
            var urgent = true;
            (string Conversation, string Text, string? Device, string Id)? deferred = null;
            try
            {
                var parameters = message.Field("params");
                id = parameters.Text("threadId");
                if (method == "account/rateLimits/updated") { usage.Update(parameters); continue; }
                if (method == "pocketagent/disconnected")
                {
                    foreach (var state in states.Values)
                    {
                        await state.Gate.WaitAsync(stoppingToken);
                        try
                        {
                            state.Status = "disconnected";
                            state.Error = "The desktop lost its connection to Codex. Restart the relay; unfinished work will not be automatically resubmitted.";
                            state.ActiveTurnId = null;
                            state.Requests.Clear();
                        }
                        finally { state.Gate.Release(); }
                    }
                    id = null;
                }
                else if (states.TryGetValue(id, out var state))
                {
                    await state.Gate.WaitAsync(stoppingToken);
                    try
                    {
                        if (message.TryGetProperty("id", out var requestId))
                        {
                            var key = requestId.GetRawText();
                            var supported = method is "item/commandExecution/requestApproval" or "item/fileChange/requestApproval" or "item/tool/requestUserInput";
                            // A grantRoot can broaden access beyond this file action; defer that host interaction.
                            if (method == "item/fileChange/requestApproval" && parameters.Text("grantRoot").Length > 0)
                                supported = false;
                            string[] decisions = method == "item/tool/requestUserInput" ? [] : ["accept", "decline", "cancel"];
                            if (parameters.Field("availableDecisions") is { ValueKind: JsonValueKind.Array } choices)
                                decisions = decisions.Where(decision => choices.EnumerateArray().Any(choice => choice.ValueKind == JsonValueKind.String && choice.GetString() == decision)).ToArray();
                            var request = new PendingRequest(key, method, PublicRequestParameters(parameters), decisions, supported);
                            state.Requests[key] = (requestId.Clone(), request);
                            state.Status = "awaiting-input";
                        }
                        else
                        {
                            switch (method)
                            {
                                case "turn/started":
                                    state.ActiveTurnId = parameters.Field("turn").Text("id");
                                    state.Status = state.Requests.Count > 0 ? "awaiting-input" : "running";
                                    break;
                                case "turn/completed":
                                    var turn = parameters.GetProperty("turn");
                                    var completedItems = turn.Field("items");
                                    var finalHasWork = completedItems.ValueKind == JsonValueKind.Array && completedItems.EnumerateArray().Any(item =>
                                        item.Text("type") != "userMessage" && (item.Text("type") != "agentMessage" || item.Text("text").Length > 0));
                                    if (turn.Field("error").Text("codexErrorInfo") == "usageLimitExceeded" && !state.HasWork && !finalHasWork && state.ActivePrompt is not null)
                                        deferred = (state.Reference.Id, state.ActivePrompt, state.SourceDeviceId, state.ScheduledMessageId ?? $"usage-{state.Reference.Id}-{turn.Text("id")}");
                                    AddTurn(state, turn);
                                    state.Status = turn.Text("status", "failed");
                                    state.Error = turn.Field("error").Text("message") is { Length: > 0 } error ? error : null;
                                    state.ActiveTurnId = null;
                                state.HeartbeatDueAt = null;
                                state.HiddenPrompts.Clear();
                                    state.Requests.Clear();
                                    break;
                                case "item/started":
                                case "item/completed":
                                    Upsert(state, Render(parameters.GetProperty("item")));
                                    break;
                                case "item/agentMessage/delta":
                                    var itemId = parameters.Text("itemId");
                                    var existing = state.Items.FirstOrDefault(item => item.Id == itemId) ?? new(itemId, "agentMessage", "", "", "inProgress");
                                    Upsert(state, existing with { Text = existing.Text + parameters.Text("delta") });
                                    urgent = false;
                                    break;
                                case "item/commandExecution/outputDelta":
                                    var commandId = parameters.Text("itemId");
                                    var command = state.Items.FirstOrDefault(item => item.Id == commandId) ?? new(commandId, "commandExecution", "", "", "inProgress");
                                    Upsert(state, command with { Detail = command.Detail + parameters.Text("delta") });
                                    urgent = false;
                                    break;
                                case "serverRequest/resolved":
                                    if (parameters.TryGetProperty("requestId", out var resolved))
                                        state.Requests.Remove(resolved.GetRawText());
                                    if (state.Requests.Count == 0 && state.ActiveTurnId is not null)
                                        state.Status = "running";
                                    break;
                                case "error":
                                    state.Error = parameters.Field("error").Text("message", "Codex reported an error.");
                                    break;
                            }
                        }
                    }
                    finally { state.Gate.Release(); }
                }
                else if (message.TryGetProperty("id", out _))
                {
                    foreach (var active in states.Values.Where(state => state.ActiveTurnId is not null))
                    {
                        await active.Gate.WaitAsync(stoppingToken);
                        try { active.Error = "Codex requested an unsupported host interaction. Interrupt this turn and complete the setup on the desktop."; }
                        finally { active.Gate.Release(); }
                    }
                    id = null;
                }
            }
            finally { gate.Release(); }
            if (deferred is { } pending)
            {
                var limits = await usage.Check(true);
                var due = limits.NextResetAt ?? DateTimeOffset.UtcNow.AddMinutes(5);
                try
                {
                    store.Schedule(pending.Conversation, pending.Text, due, "usage-reset", pending.Device, pending.Id);
                    store.UpdateSchedule(pending.Id, "pending", "Waiting for usage reset.", due);
                }
                catch (ApiException exception) when (exception.Status == 409) { /* Archiving won the race; do not recreate its canceled work. */ }
            }
            var now = Environment.TickCount64;
            if (urgent || now - lastPublished >= 100)
            {
                lastPublished = now;
                await publisher.Changed(id);
            }
        }
    }
}
