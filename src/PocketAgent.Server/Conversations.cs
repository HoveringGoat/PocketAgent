using System.Text.Json;

namespace PocketAgent;

public sealed record DisplayItem(string Id, string Type, string Text, string Detail, string Status, DateTimeOffset? Timestamp = null, string? ClientMessageId = null);
public sealed record PendingRequest(string Id, string Method, JsonElement Parameters, string[] Decisions, bool Supported);
public sealed record ConversationSummary(string Id, string Title, DateTimeOffset CreatedAt, string Status, string Repository, string WorkingDirectory, string HostName, string? Model, bool Archived, string? ReasoningEffort = null);
public sealed record ConversationSnapshot(string Id, string Title, string Status, string? ActiveTurnId, DisplayItem[] Items, PendingRequest[] Requests, string? Error, string Repository, string WorkingDirectory, string HostName, string PermissionMode, string? SandboxMode, string? ApprovalPolicy, DateTimeOffset? ActiveTurnStartedAt, long? LastWorkDurationMs, string? Model, bool Archived, string? ReasoningEffort = null, bool HistoryTruncated = false, string? HistoryBefore = null);
public sealed record ConversationHistoryPage(DisplayItem[] Items, bool HasMore, string? Before);
public sealed record AnswerRequest(string? Decision, Dictionary<string, string[]>? Answers);

public sealed class Conversations(CodexConnection codex, LocalStore store, ProgressPublisher publisher, HostSettings settings, ProviderUsage usage, WorkspaceDirectories directories, ProviderDiagnostics diagnostics) : BackgroundService
{
    private const int MaxSnapshotItems = 200;
    private const int MaxSnapshotText = 8_000;
    private const int MaxSnapshotDetail = 16_000;
    private const int DefaultHistoryPageSize = 50;
    private const int MaxHistoryPageSize = 100;
    private sealed class State(ConversationReference reference)
    {
        public ConversationReference Reference { get; set; } = reference;
        public string Status { get; set; } = "idle";
        public string? ActiveTurnId { get; set; }
        public string? Error { get; set; }
        public bool Resumed { get; set; }
        public string? SandboxMode { get; set; }
        public string? ApprovalPolicy { get; set; }
        public DateTimeOffset? ActiveTurnStartedAt { get; set; }
        public long? LastWorkDurationMs { get; set; }
        public bool Hydrating { get; set; }
        public bool HasWork { get; set; }
        public string? ActivePrompt { get; set; }
        public string? SourceDeviceId { get; set; }
        public string? ScheduledMessageId { get; set; }
        public Queue<(string Id, string Text)> ClientMessages { get; } = [];
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public List<DisplayItem> Items { get; } = [];
        public Dictionary<string, (JsonElement RpcId, PendingRequest Request)> Requests { get; } = [];
    }

    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly Dictionary<string, State> states = [];
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, Task<State>> readHydrations = [];
    private long lastPublished;

    public async Task<ConversationSummary[]> List(bool archived = false)
    {
        await gate.WaitAsync();
        try
        {
            return store.Conversations(true).Where(reference => reference.Archived == archived).Select(reference => new ConversationSummary(reference.Id, reference.Title, reference.CreatedAt,
                states.TryGetValue(reference.Id, out var state) ? state.Status : "saved", Path.GetFileName(reference.WorkingDirectory), reference.WorkingDirectory, Environment.MachineName, reference.Model, reference.Archived, reference.ReasoningEffort)).ToArray();
        }
        finally { gate.Release(); }
    }

    public async Task<ConversationSnapshot> Create(string title, string? workingDirectory = null, string? model = null, string? reasoningEffort = null)
    {
        if (title.Length > 100)
            throw new ApiException(400, "Conversation names can contain at most 100 characters.");
        var directory = directories.Resolve(workingDirectory);
        if (model is not null) await ValidateModel(model);
        if (reasoningEffort is not null) await ValidateReasoning(model, reasoningEffort);
        await gate.WaitAsync();
        try
        {
            if (string.IsNullOrWhiteSpace(title))
            {
                var baseName = Path.GetFileName(Path.TrimEndingDirectorySeparator(directory));
                if (baseName.Length == 0) baseName = "New conversation";
                baseName = baseName[..Math.Min(baseName.Length, 85)];
                var existing = store.Conversations().Select(row => row.Title).ToHashSet(StringComparer.OrdinalIgnoreCase);
                title = baseName;
                for (var suffix = 2; existing.Contains(title); suffix++) title = $"{baseName} · {suffix}";
            }
            await codex.EnsureStarted();
            // Model selection belongs to this conversation; reviewer and permission selection remain desktop-owned.
            var parameters = CodexPermissions.Apply(settings, new { cwd = directory });
            if (model is not null) parameters["model"] = model;
            var result = await codex.Request("thread/start", parameters);
            var thread = result.GetProperty("thread");
            var id = thread.Text("id");
            if (id.Length == 0)
                throw new ApiException(502, "Codex returned a thread without an ID.");
            store.SaveConversation(id, title.Trim(), directory, model, reasoningEffort);
            var state = new State(new(id, title.Trim(), DateTimeOffset.UtcNow, directory, model, false, reasoningEffort)) { Resumed = true };
            CapturePermissions(state, result);
            states[id] = state;
            return Snapshot(state);
        }
        finally { gate.Release(); await publisher.Changed(); }
    }

    public async Task<ConversationSnapshot> Get(string id)
    {
        var hydrated = await GetState(id).ConfigureAwait(false);
        await hydrated.Gate.WaitAsync();
        try { return Snapshot(hydrated); }
        finally { hydrated.Gate.Release(); }
    }

    public async Task<ConversationHistoryPage> History(string id, string? before, int limit)
    {
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

    public async Task<ConversationSnapshot> Rename(string id, string title)
    {
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

    private static void RequireIdle(State state)
    {
        if (state.ActiveTurnId is not null || state.Status is "starting" or "uncertain" or "disconnected" || state.Requests.Count > 0)
            throw new ApiException(409, "Finish or stop active work and confirm its outcome before changing this conversation.");
    }

    public async Task<ConversationSnapshot> SetModel(string id, string model)
    {
        var state = await GetState(id);
        await state.Gate.WaitAsync();
        try
        {
            RequireUnarchived(state); RequireIdle(state);
            await ValidateModel(model);
            store.SetModel(id, model);
            var supported = (await diagnostics.Models()).First(option => option.Model == model).SupportedReasoningEfforts;
            var reasoning = state.Reference.ReasoningEffort is not null && supported.Contains(state.Reference.ReasoningEffort, StringComparer.OrdinalIgnoreCase) ? state.Reference.ReasoningEffort : null;
            if (reasoning is null && state.Reference.ReasoningEffort is not null) store.SetReasoningEffort(id, null);
            state.Reference = state.Reference with { Model = model, ReasoningEffort = reasoning };
            return Snapshot(state);
        }
        finally { state.Gate.Release(); await publisher.Changed(id); }
    }

    public async Task<ConversationSnapshot> SetReasoning(string id, string? effort)
    {
        var state = await GetState(id);
        await state.Gate.WaitAsync();
        try
        {
            RequireUnarchived(state); RequireIdle(state);
            if (effort is not null) await ValidateReasoning(state.Reference.Model, effort);
            store.SetReasoningEffort(id, effort);
            state.Reference = state.Reference with { ReasoningEffort = effort };
            return Snapshot(state);
        }
        finally { state.Gate.Release(); await publisher.Changed(id); }
    }

    public async Task<ConversationSnapshot> Archive(string id, bool archived)
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
        finally { state.Gate.Release(); await publisher.Changed(id); }
    }

    public async Task StartTurn(string id, string text, string? expectedTurnId = null, string? sourceDeviceId = null, string? scheduledMessageId = null, string? clientMessageId = null)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > 32_000)
            throw new ApiException(400, "Messages must contain 1–32,000 characters.");
        var state = await GetState(id);
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
                        input = new[] { new { type = "text", text, text_elements = Array.Empty<object>() } }
                    });
                    if (result.Text("turnId") != expectedTurnId)
                        throw new ApiException(504, "Codex did not confirm the expected turn. Steering delivery is unknown; do not resend automatically.");
                    if (!string.IsNullOrWhiteSpace(clientMessageId)) state.ClientMessages.Enqueue((clientMessageId, text));
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
            state.ActivePrompt = text; state.SourceDeviceId = sourceDeviceId; state.ScheduledMessageId = scheduledMessageId;
            if (!string.IsNullOrWhiteSpace(clientMessageId)) state.ClientMessages.Enqueue((clientMessageId, text));
            state.HasWork = false; state.ActiveTurnStartedAt = DateTimeOffset.UtcNow;
            try
            {
                var parameters = CodexPermissions.Apply(settings, new
                {
                    threadId = id,
                    cwd = directories.Resolve(state.Reference.WorkingDirectory),
                    input = new[] { new { type = "text", text, text_elements = Array.Empty<object>() } }
                }, turn: true, workingDirectory: state.Reference.WorkingDirectory);
                if (state.Reference.Model is not null) parameters["model"] = state.Reference.Model;
                if (state.Reference.ReasoningEffort is not null) parameters["effort"] = state.Reference.ReasoningEffort;
                var result = await codex.Request("turn/start", parameters);
                var turn = result.GetProperty("turn");
                if (turn.Text("id").Length == 0) throw new ApiException(504, "Codex did not confirm a turn ID. Delivery is unknown; do not resend automatically.");
                state.ActiveTurnId = turn.Text("id");
                state.Status = "running";
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

    public async Task Interrupt(string id)
    {
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

    public async Task Answer(string id, string requestId, AnswerRequest answer)
    {
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
                if (!state.Hydrating && rendered.Type == "userMessage" && state.ClientMessages.TryPeek(out var pending) && string.Equals(pending.Text, rendered.Text, StringComparison.Ordinal))
                {
                    state.ClientMessages.Dequeue();
                    rendered = rendered with { ClientMessageId = pending.Id };
                }
                Upsert(state, rendered);
            }
        }
    }

    private void Upsert(State state, DisplayItem item)
    {
        if (item.Id.Length == 0 || item.Type == "reasoning")
            return;
        var index = state.Items.FindIndex(existing => existing.Id == item.Id);
        var existingTime = index >= 0 ? state.Items[index].Timestamp : null;
        item = item with { Timestamp = existingTime ?? store.RememberTimestamp(state.Reference.Id, item.Id, item.Timestamp ?? (state.Hydrating ? null : DateTimeOffset.UtcNow)) };
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
        return new(item.Text("id"), type, text, detail, item.Text("status"));
    }

    private ConversationSnapshot Snapshot(State state)
    {
        var first = Math.Max(0, state.Items.Count - MaxSnapshotItems);
        var historyTruncated = first > 0;
        var items = state.Items.Skip(first).Select(Bounded).ToArray();
        return new(state.Reference.Id, state.Reference.Title, state.Status, state.ActiveTurnId,
            items, state.Requests.Values.Select(entry => entry.Request).ToArray(), state.Error, Path.GetFileName(state.Reference.WorkingDirectory), state.Reference.WorkingDirectory, Environment.MachineName,
            settings.PermissionMode, state.SandboxMode, state.ApprovalPolicy, state.ActiveTurnId is null ? null : state.ActiveTurnStartedAt, state.LastWorkDurationMs, state.Reference.Model, state.Reference.Archived, state.Reference.ReasoningEffort, historyTruncated, historyTruncated ? state.Items[first].Id : null);
    }

    private static DisplayItem Bounded(DisplayItem item) => item with { Text = Limit(item.Text, MaxSnapshotText), Detail = Limit(item.Detail, MaxSnapshotDetail) };

    private static string Limit(string value, int length) => value.Length <= length ? value : value[..length] + "\n[output truncated]";

    private static DateTimeOffset? Timestamp(JsonElement value) => value.TryGetInt64Safe(out var seconds) && seconds > 0 && seconds < 253402300799 ? DateTimeOffset.FromUnixTimeSeconds(seconds) : null;

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
