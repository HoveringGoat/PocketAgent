namespace PocketAgent;

public sealed record MessageReceipt(bool Queued, string? ScheduleId = null, DateTimeOffset? DueAt = null, string? Message = null);

public sealed class ScheduledMessages(LocalStore store, Conversations conversations, ProviderUsage usage, CodexConnection codex,
    ProgressPublisher publisher, IHostApplicationLifetime application) : BackgroundService
{
    public async Task<MessageReceipt> Send(string id, string text, string? expectedTurnId, string? deviceId)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > 32000) throw new ApiException(400, "Messages must contain 1–32,000 characters.");
        var state = await conversations.Get(id);
        if (state.Archived) throw new ApiException(409, "Restore this conversation before sending messages.");
        if (state.Status is "starting" or "uncertain" or "disconnected") throw new ApiException(409, "Wait until the conversation state is confirmed before submitting.");
        if (expectedTurnId is not null && state.ActiveTurnId != expectedTurnId || expectedTurnId is null && state.ActiveTurnId is not null)
            throw new ApiException(409, "Conversation activity changed. Refresh before submitting.");
        var limits = await usage.Check();
        if (limits.Exhausted) return await Defer(id, text, limits, deviceId);
        try { await conversations.StartTurn(id, text, expectedTurnId, deviceId); }
        catch (ApiException exception) when (exception.Status == 429)
        { return await Defer(id, text, await usage.Check(true), deviceId); }
        return new(false);
    }

    private async Task<MessageReceipt> Defer(string id, string text, UsageStatus limits, string? deviceId)
    {
        var due = limits.NextResetAt ?? DateTimeOffset.UtcNow.AddMinutes(5);
        var job = store.Schedule(id, text, due, "usage-reset", deviceId);
        await publisher.Changed(id);
        return new(true, job.Id, limits.NextResetAt, limits.NextResetAt is null
            ? "Queued until usage becomes available. The provider has not reported a reset time."
            : "Queued for after usage resets. The desktop must remain running.");
    }

    public async Task<ScheduledMessage> Schedule(string id, string text, DateTimeOffset due, string? deviceId)
    {
        var job = store.Schedule(id, text, due, "scheduled", deviceId);
        await publisher.Changed(id); return job;
    }
    public async Task Cancel(string id)
    {
        if (!store.UpdateSchedule(id, "canceled", expectedStatus: "pending"))
            throw new ApiException(409, "The message is no longer pending. A message already dispatching cannot be recalled.");
        await publisher.Changed();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = application.ApplicationStarted.Register(() => ready.TrySetResult());
        await ready.Task.WaitAsync(stoppingToken);
        // A crashed sender may have written to Codex without persisting an acknowledgement.
        foreach (var job in store.Scheduled().Where(job => job.Status == "dispatching"))
            store.UpdateSchedule(job.Id, "uncertain", "The host restarted during delivery. Review history; this message will not be replayed.", expectedStatus: "dispatching");
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        do
        {
            foreach (var job in store.Scheduled().Where(job => job.Status == "pending"))
            {
                if (stoppingToken.IsCancellationRequested) return;
                if (job.DeviceId is not null && !store.IsDeviceActive(job.DeviceId))
                { store.UpdateSchedule(job.Id, "canceled", "Device access expired or was revoked.", expectedStatus: "pending"); continue; }
                if (job.NextAttemptAt > DateTimeOffset.UtcNow) continue;
                try
                {
                    var state = await conversations.Get(job.ConversationId);
                    if (state.ActiveTurnId is not null || state.Status is "starting" or "uncertain" or "disconnected" || state.Requests.Length > 0)
                    { store.UpdateSchedule(job.Id, "pending", "Waiting for the conversation to become idle.", DateTimeOffset.UtcNow.AddSeconds(5), "pending"); continue; }
                    var limits = await usage.Check(force: job.Reason == "usage-reset");
                    if (limits.Exhausted || !codex.IsReady)
                    { store.UpdateSchedule(job.Id, "pending", "Waiting for usage or provider availability.", limits.NextResetAt ?? DateTimeOffset.UtcNow.AddSeconds(30), "pending"); continue; }
                    if (!store.UpdateSchedule(job.Id, "dispatching", expectedStatus: "pending")) continue;
                    try
                    {
                        await conversations.StartTurn(job.ConversationId, job.Text, sourceDeviceId: job.DeviceId, scheduledMessageId: job.Id);
                        store.UpdateSchedule(job.Id, "submitted", expectedStatus: "dispatching");
                    }
                    catch (ApiException exception)
                    {
                        var status = exception.Status is 503 or 504 ? "uncertain" : exception.Status is 409 or 429 ? "pending" : "failed";
                        var reset = exception.Status == 429 ? (await usage.Check(true)).NextResetAt : null;
                        store.UpdateSchedule(job.Id, status, exception.Message, reset ?? DateTimeOffset.UtcNow.AddSeconds(30), "dispatching");
                    }
                    await publisher.Changed(job.ConversationId);
                }
                catch (ApiException) { store.UpdateSchedule(job.Id, "pending", "Waiting for the provider.", DateTimeOffset.UtcNow.AddSeconds(30), "pending"); }
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
