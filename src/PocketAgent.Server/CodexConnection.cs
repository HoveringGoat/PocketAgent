using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace PocketAgent;

public sealed class CodexConnection(HostSettings settings) : IAsyncDisposable
{
    private readonly SemaphoreSlim startGate = new(1, 1);
    private readonly SemaphoreSlim writeGate = new(1, 1);
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonElement>> pending = new();
    private readonly Channel<JsonElement> events = Channel.CreateUnbounded<JsonElement>(new() { SingleReader = true, SingleWriter = true });
    private readonly CancellationTokenSource lifetime = new();
    private Process? process;
    private Task? reader;
    private Task? stderr;
    private long sequence;
    private bool ready;
    private bool failed;
    public CodexExecutable? Executable { get; private set; }
    public ChannelReader<JsonElement> Events => events.Reader;
    public bool IsReady => ready && !failed;

    public async Task EnsureStarted()
    {
        await startGate.WaitAsync(lifetime.Token);
        try
        {
            if (failed)
                throw new ApiException(503, "The Codex connection was lost. Restart the desktop relay to reconnect; previous work will not be resubmitted.");
            if (ready)
                return;
            Executable = CodexExecutableResolver.Resolve(settings);
            var info = new ProcessStartInfo(Executable.Path)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardInputEncoding = new UTF8Encoding(false),
                StandardOutputEncoding = new UTF8Encoding(false),
                StandardErrorEncoding = new UTF8Encoding(false),
                WorkingDirectory = settings.RepositoryPath
            };
            foreach (var argument in settings.CodexArguments)
                info.ArgumentList.Add(argument);
            process = Process.Start(info) ?? throw new InvalidOperationException("Unable to launch Codex.");
            reader = ReadLoop();
            stderr = DrainErrors();
            await Request("initialize", new { clientInfo = new { name = "pocketagent", title = "PocketAgent", version = BuildInfo.Version } });
            await Send(new { method = "initialized" });
            ready = true;
        }
        catch (Exception exception) when (exception is not ApiException)
        {
            failed = true;
            throw new ApiException(503, "Could not initialize Codex. Check the desktop CodexExecutable setting, CLI sign-in, and CODEX_HOME, then restart the relay.");
        }
        finally
        {
            startGate.Release();
        }
    }

    public async Task<JsonElement> Request(string method, object parameters)
    {
        var id = Interlocked.Increment(ref sequence);
        var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        pending[id] = completion;
        try
        {
            await Send(new { id, method, @params = parameters });
            return await completion.Task.WaitAsync(TimeSpan.FromSeconds(60), lifetime.Token);
        }
        catch (TimeoutException)
        {
            throw new ApiException(504, "Codex did not confirm the request in time. Its outcome is unknown; do not automatically resend it. Check progress or restart the relay.");
        }
        finally
        {
            pending.TryRemove(id, out _);
        }
    }

    public Task Respond(JsonElement id, object result) => Send(new { id, result });

    private async Task Send(object message)
    {
        await writeGate.WaitAsync(lifetime.Token);
        try
        {
            if (process is null || process.HasExited)
                throw new ApiException(503, "Codex is disconnected. Restart the desktop relay.");
            await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(message));
            await process.StandardInput.FlushAsync();
        }
        catch (IOException)
        {
            throw new ApiException(503, "Codex is disconnected. Restart the desktop relay.");
        }
        finally
        {
            writeGate.Release();
        }
    }

    private async Task ReadLoop()
    {
        try
        {
            while (await process!.StandardOutput.ReadLineAsync(lifetime.Token) is { } line)
            {
                using var document = JsonDocument.Parse(line);
                var message = document.RootElement;
                if (!message.TryGetProperty("method", out _) && message.TryGetProperty("id", out var id) && id.TryGetInt64(out var number) && pending.TryRemove(number, out var completion))
                {
                    if (message.TryGetProperty("error", out var error))
                        completion.TrySetException(new ApiException(error.Field("data").Text("codexErrorInfo") == "usageLimitExceeded" ? 429 : 502,
                            "Codex: " + error.Text("message", "Provider request failed.")));
                    else
                        completion.TrySetResult(message.GetProperty("result").Clone());
                }
                else if (message.TryGetProperty("method", out _))
                    await events.Writer.WriteAsync(message.Clone(), lifetime.Token);
            }
        }
        catch (Exception exception) when (exception is IOException or JsonException or OperationCanceledException) { }
        finally
        {
            failed = true;
            ready = false;
            foreach (var completion in pending.Values)
                completion.TrySetException(new ApiException(503, "Codex disconnected before confirming the request. Its outcome may be unknown."));
            events.Writer.TryWrite(JsonSerializer.SerializeToElement(new { method = "pocketagent/disconnected" }));
        }
    }

    private async Task DrainErrors()
    {
        // Provider stderr can contain sensitive context. Drain it without copying it to host logs.
        try
        {
            while (await process!.StandardError.ReadLineAsync(lifetime.Token) is not null) { }
        }
        catch (OperationCanceledException) { }
    }

    public async ValueTask DisposeAsync()
    {
        lifetime.Cancel();
        if (process is { HasExited: false })
            process.Kill(entireProcessTree: true);
        if (reader is not null)
            await reader;
        if (stderr is not null)
            await stderr;
        process?.Dispose();
        lifetime.Dispose();
        startGate.Dispose();
        writeGate.Dispose();
    }
}

public static class JsonFields
{
    public static bool TryGetInt64Safe(this JsonElement element, out long value) { value = 0; return element.ValueKind == JsonValueKind.Number && element.TryGetInt64(out value); }
    public static bool TryGetInt32Safe(this JsonElement element, out int value) { value = 0; return element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out value); }
    public static string Text(this JsonElement element, string name, string fallback = "") =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString()! : fallback;

    public static JsonElement Field(this JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) ? value : default;
}
