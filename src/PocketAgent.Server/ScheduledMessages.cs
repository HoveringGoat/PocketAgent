namespace PocketAgent;

public sealed record MessageReceipt(bool Queued, string? ScheduleId = null, DateTimeOffset? DueAt = null, string? Message = null, string? ClientMessageId = null, string DeliveryStatus = "accepted");

public sealed class ScheduledMessages(LocalStore store, Conversations conversations, ProviderUsage usage, CodexConnection codex,
    ProgressPublisher publisher, IHostApplicationLifetime application, ILogger<ScheduledMessages> logger) : BackgroundService
{
    public async Task<MessageReceipt> Send(string id, string text, string? expectedTurnId, string? deviceId, string? clientMessageId = null)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > 32000) throw new ApiException(400, "Messages must contain 1–32,000 characters.");
        if (!string.IsNullOrWhiteSpace(clientMessageId) && clientMessageId.Length > 100) throw new ApiException(400, "Message IDs are too long.");
        if (!string.IsNullOrWhiteSpace(clientMessageId))
        {
            var existing = store.Delivery(id, clientMessageId);
            if (existing is not null)
            {
                if (!string.Equals(existing.Text, text, StringComparison.Ordinal)) throw new ApiException(409, "That message ID was already used for different text.");
                return Receipt(existing);
            }
            if (!store.BeginDelivery(id, clientMessageId, text))
                return Receipt(store.Delivery(id, clientMessageId) ?? throw new ApiException(409, "That message is already being delivered."));
        }
        var state = await conversations.Get(id);
        if (state.Archived) throw new ApiException(409, "Restore this conversation before sending messages.");
        if (state.Status is "starting" or "uncertain" or "disconnected") throw new ApiException(409, "Wait until the conversation state is confirmed before submitting.");
        if (expectedTurnId is not null && state.ActiveTurnId != expectedTurnId || expectedTurnId is null && state.ActiveTurnId is not null)
            throw new ApiException(409, "Conversation activity changed. Refresh before submitting.");
        var limits = await usage.Check();
        if (limits.Exhausted) return await Defer(id, text, limits, deviceId, clientMessageId);
        try { await conversations.StartTurn(id, text, expectedTurnId, deviceId, clientMessageId: clientMessageId); }
        catch (ApiException exception) when (exception.Status == 429)
        { return await Defer(id, text, await usage.Check(true), deviceId, clientMessageId); }
        catch (ApiException exception)
        {
            if (!string.IsNullOrWhiteSpace(clientMessageId)) store.UpdateDelivery(id, clientMessageId, exception.Status is 503 or 504 ? "uncertain" : "failed", error: exception.Message);
            throw;
        }
        if (!string.IsNullOrWhiteSpace(clientMessageId)) store.UpdateDelivery(id, clientMessageId, "accepted");
        return new(false, ClientMessageId: clientMessageId, DeliveryStatus: "accepted");
    }

    private async Task<MessageReceipt> Defer(string id, string text, UsageStatus limits, string? deviceId, string? clientMessageId)
    {
        var due = limits.NextResetAt ?? DateTimeOffset.UtcNow.AddMinutes(5);
        var job = store.Schedule(id, text, due, "usage-reset", deviceId, clientMessageId);
        if (!string.IsNullOrWhiteSpace(clientMessageId)) store.UpdateDelivery(id, clientMessageId, "queued", job.Id, "Waiting for usage reset.");
        await publisher.Changed(id);
        return new(true, job.Id, limits.NextResetAt, limits.NextResetAt is null
            ? "Queued until usage becomes available. The provider has not reported a reset time."
            : "Queued for after usage resets. The desktop must remain running.", clientMessageId, "queued");
    }

    public async Task<ScheduledMessage> Schedule(string id, string text, DateTimeOffset due, string? deviceId, string? clientMessageId = null)
    {
        if (!string.IsNullOrWhiteSpace(clientMessageId))
        {
            var existing = store.Delivery(id, clientMessageId);
            if (existing is not null)
            {
                if (!string.Equals(existing.Text, text, StringComparison.Ordinal)) throw new ApiException(409, "That message ID was already used for different text.");
                return store.Scheduled(id).FirstOrDefault(job => job.Id == existing.ScheduleId) ?? throw new ApiException(409, "That scheduled message is no longer available.");
            }
            if (!store.BeginDelivery(id, clientMessageId, text))
                return store.Scheduled(id).FirstOrDefault(job => job.Id == clientMessageId) ?? throw new ApiException(409, "That message is already being scheduled.");
        }
        var job = store.Schedule(id, text, due, "scheduled", deviceId, clientMessageId);
        if (!string.IsNullOrWhiteSpace(clientMessageId)) store.UpdateDelivery(id, clientMessageId, "queued", job.Id);
        await publisher.Changed(id); return job;
    }

    private static MessageReceipt Receipt(MessageDelivery delivery)
    {
        var status = delivery.Status is "pending" or "dispatching" ? "uncertain" : delivery.Status;
        return new(status == "queued", delivery.ScheduleId, null, delivery.Error, delivery.ClientMessageId, status);
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
        {
            store.UpdateSchedule(job.Id, "uncertain", "The host restarted during delivery. Review history; this message will not be replayed.", expectedStatus: "dispatching");
            store.UpdateDelivery(job.ConversationId, job.Id, "uncertain", job.Id, "The host restarted during delivery.");
        }
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        do
        {
            foreach (var job in store.Scheduled().Where(job => job.Status == "pending"))
            {
                if (stoppingToken.IsCancellationRequested) return;
                if (job.DeviceId is not null && !store.IsDeviceActive(job.DeviceId))
                { store.UpdateSchedule(job.Id, "canceled", "Device access expired or was revoked.", expectedStatus: "pending"); continue; }
                if (job.NextAttemptAt > DateTimeOffset.UtcNow) continue;
                var claimed = false;
                try
                {
                    var state = await conversations.Get(job.ConversationId);
                    if (state.ActiveTurnId is not null || state.Status is "starting" or "uncertain" or "disconnected" || state.Requests.Length > 0)
                    { store.UpdateSchedule(job.Id, "pending", "Waiting for the conversation to become idle.", DateTimeOffset.UtcNow.AddSeconds(5), "pending"); continue; }
                    var limits = await usage.Check(force: job.Reason == "usage-reset");
                    if (limits.Exhausted || !codex.IsReady)
                    { store.UpdateSchedule(job.Id, "pending", "Waiting for usage or provider availability.", limits.NextResetAt ?? DateTimeOffset.UtcNow.AddSeconds(30), "pending"); continue; }
                    if (!store.UpdateSchedule(job.Id, "dispatching", expectedStatus: "pending")) continue;
                    claimed = true;
                    store.UpdateDelivery(job.ConversationId, job.Id, "dispatching", job.Id);
                    try
                    {
                        await conversations.StartTurn(job.ConversationId, job.Text, sourceDeviceId: job.DeviceId, scheduledMessageId: job.Id, clientMessageId: job.Id);
                        store.UpdateSchedule(job.Id, "submitted", expectedStatus: "dispatching");
                        store.UpdateDelivery(job.ConversationId, job.Id, "accepted", job.Id);
                    }
                    catch (ApiException exception)
                    {
                        var status = exception.Status is 503 or 504 ? "uncertain" : exception.Status is 409 or 429 ? "pending" : "failed";
                        var reset = exception.Status == 429 ? (await usage.Check(true)).NextResetAt : null;
                        store.UpdateSchedule(job.Id, status, exception.Message, reset ?? DateTimeOffset.UtcNow.AddSeconds(30), "dispatching");
                        store.UpdateDelivery(job.ConversationId, job.Id, status is "uncertain" ? "uncertain" : status, job.Id, exception.Message);
                    }
                    await publisher.Changed(job.ConversationId);
                }
                catch (ApiException exception)
                {
                    var status = claimed ? "uncertain" : "pending";
                    var expected = claimed ? "dispatching" : "pending";
                    var message = claimed
                        ? $"Scheduled delivery outcome is unknown after a host error: {exception.Message} Review the conversation before retrying."
                        : $"Waiting for the provider: {exception.Message}";
                    try { store.UpdateSchedule(job.Id, status, message, DateTimeOffset.UtcNow.AddSeconds(30), expected); }
                    catch (Exception persistenceException) { logger.LogError(persistenceException, "Could not persist scheduled message {JobId} after an API failure.", job.Id); }
                    if (claimed) store.UpdateDelivery(job.ConversationId, job.Id, "uncertain", job.Id, message);
                }
                catch (Exception exception)
                {
                    logger.LogError(exception, "Scheduled message {JobId} failed in the worker.", job.Id);
                    var status = claimed ? "uncertain" : "pending";
                    var expected = claimed ? "dispatching" : "pending";
                    var message = claimed
                        ? "Scheduled delivery outcome is unknown after an unexpected host error. Review the conversation before retrying."
                        : "The scheduled message worker encountered an unexpected error; it will retry.";
                    try { store.UpdateSchedule(job.Id, status, message, DateTimeOffset.UtcNow.AddSeconds(30), expected); }
                    catch (Exception persistenceException) { logger.LogError(persistenceException, "Could not persist scheduled message {JobId} after an unexpected failure.", job.Id); }
                    if (claimed) store.UpdateDelivery(job.ConversationId, job.Id, "uncertain", job.Id, message);
                }
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
