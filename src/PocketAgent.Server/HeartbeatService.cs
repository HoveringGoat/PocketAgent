namespace PocketAgent;

public sealed class HeartbeatService(Conversations conversations, IHostApplicationLifetime application, ILogger<HeartbeatService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = application.ApplicationStarted.Register(() => ready.TrySetResult());
        await ready.Task.WaitAsync(stoppingToken);
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(15));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            foreach (var id in await conversations.ActiveConversationIds())
            {
                try { await conversations.TryHeartbeat(id); }
                catch (Exception exception) { logger.LogWarning(exception, "Heartbeat processing failed for conversation {ConversationId}.", id); }
            }
        }
    }
}
