namespace PocketAgent;

/// <summary>Applies periodic registry maintenance without loading provider history for every conversation.</summary>
public sealed class ConversationMaintenance(Conversations conversations, IHostApplicationLifetime application, ILogger<ConversationMaintenance> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = application.ApplicationStarted.Register(() => ready.TrySetResult());
        await ready.Task.WaitAsync(stoppingToken);
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(10));
        do
        {
            try { await conversations.ArchiveInactive(DateTimeOffset.UtcNow); }
            catch (Exception exception) when (!stoppingToken.IsCancellationRequested)
            { logger.LogWarning(exception, "Conversation inactivity maintenance failed."); }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
