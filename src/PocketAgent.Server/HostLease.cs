namespace PocketAgent;

public sealed class HostLease(HostSettings settings) : IHostedService, IDisposable
{
    private FileStream? lease;
    private readonly object gate = new();
    public void Acquire()
    {
        lock (gate)
        {
            if (lease is not null) return;
            try { lease = new FileStream(Path.Combine(settings.DataDirectory, "host.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) { throw new InvalidOperationException("Another PocketAgent host is using this runtime directory. Stop it before starting another server."); }
        }
    }
    public Task StartAsync(CancellationToken cancellationToken)
    {
        Acquire();
        return Task.CompletedTask;
    }
    public Task StopAsync(CancellationToken cancellationToken) { Dispose(); return Task.CompletedTask; }
    public void Dispose() { lock (gate) { lease?.Dispose(); lease = null; } }
}
