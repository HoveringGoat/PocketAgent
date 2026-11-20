using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace PocketAgent;

public sealed class CodexConnection(HostSettings settings) : IAsyncDisposable
{
    private readonly SemaphoreSlim startGate = new(1, 1);
    private readonly SemaphoreSlim writeGate = new(1, 1);
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonElement>> pending = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonElement>> turnCompletions = new(StringComparer.Ordinal);
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
    public string CredentialEnvironmentVariable => settings.CodexApiKeyEnvironmentVariable;
    public bool CredentialOverride => settings.CodexApiKeyOverride;

    public ProviderCredentialInfo CredentialInfo
    {
        get
        {
            var variable = settings.CodexApiKeyEnvironmentVariable;
            var value = settings.CodexApiKeyOverride ? settings.CodexApiKey : Environment.GetEnvironmentVariable(variable);
            var source = settings.CodexApiKeyOverride
                ? value is null ? "PocketAgent override is empty" : "PocketAgent protected store"
                : value is null ? "Not detected" : $"Inherited {variable}";
            return new(source, value is not null, value is null ? null : Fingerprint(value), variable);
        }
    }

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
            if (settings.CodexApiKeyOverride)
            {
                info.Environment.Remove(settings.CodexApiKeyEnvironmentVariable);
                if (!string.IsNullOrWhiteSpace(settings.CodexApiKey))
                    info.Environment[settings.CodexApiKeyEnvironmentVariable] = settings.CodexApiKey;
            }
            foreach (var argument in settings.CodexArguments)
                info.ArgumentList.Add(argument);
            var credentialVariable = settings.CodexApiKeyEnvironmentVariable;
            var hasEffectiveCredential = info.Environment.TryGetValue(credentialVariable, out var effectiveCredential) && !string.IsNullOrWhiteSpace(effectiveCredential);
            var codexHome = info.Environment.TryGetValue("CODEX_HOME", out var configuredCodexHome) && !string.IsNullOrWhiteSpace(configuredCodexHome)
                ? configuredCodexHome
                : null;
            var credentialSource = settings.CodexApiKeyOverride
                ? hasEffectiveCredential ? "pocketagent_protected_store" : "pocketagent_override_empty"
                : hasEffectiveCredential ? "inherited_environment" : "not_detected";
            ServerDiagnostics.Log("codex", Microsoft.Extensions.Logging.LogLevel.Information,
                $"process_start executable_source={LogValue(Executable.Source)} executable_path={LogValue(Executable.Path)} arguments={settings.CodexArguments.Length} working_directory_name={LogValue(Path.GetFileName(Path.TrimEndingDirectorySeparator(settings.RepositoryPath)))} codex_home_source={(codexHome is null ? "default" : "environment")} codex_home_path={LogValue(codexHome ?? "not_set")} codex_home_exists={(codexHome is not null && Directory.Exists(codexHome)).ToString().ToLowerInvariant()} credential_source={credentialSource} credential_present={hasEffectiveCredential.ToString().ToLowerInvariant()} credential_environment_variable={credentialVariable} credential_fingerprint={LogValue(hasEffectiveCredential ? Fingerprint(effectiveCredential!) : "unavailable")}");
            process = Process.Start(info) ?? throw new InvalidOperationException("Unable to launch Codex.");
            reader = ReadLoop();
            stderr = DrainErrors();
            await Request("initialize", new { clientInfo = new { name = "pocketagent", title = "PocketAgent", version = BuildInfo.Version } });
            await Send(new { method = "initialized" });
            ready = true;
            ServerDiagnostics.Log("codex", Microsoft.Extensions.Logging.LogLevel.Information, "process_ready");
        }
        catch (ApiException exception)
        {
            failed = true;
            ServerDiagnostics.Log("codex", Microsoft.Extensions.Logging.LogLevel.Error, "process_initialize_failed", exception);
            throw;
        }
        catch (Exception exception) when (exception is not ApiException)
        {
            failed = true;
            ServerDiagnostics.Log("codex", Microsoft.Extensions.Logging.LogLevel.Error, "process_start_failed", exception);
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
        var stopwatch = Stopwatch.StartNew();
        ServerDiagnostics.Log("codex-rpc", Microsoft.Extensions.Logging.LogLevel.Information, $"request_start id={id} method={method}");
        var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        pending[id] = completion;
        try
        {
            await Send(new { id, method, @params = parameters });
            var result = await completion.Task.WaitAsync(TimeSpan.FromSeconds(60), lifetime.Token);
            ServerDiagnostics.Log("codex-rpc", Microsoft.Extensions.Logging.LogLevel.Information, $"request_ok id={id} method={method} elapsed_ms={stopwatch.ElapsedMilliseconds}");
            return result;
        }
        catch (TimeoutException)
        {
            ServerDiagnostics.Log("codex-rpc", Microsoft.Extensions.Logging.LogLevel.Error, $"request_timeout id={id} method={method} elapsed_ms={stopwatch.ElapsedMilliseconds}");
            throw new ApiException(504, "Codex did not confirm the request in time. Its outcome is unknown; do not automatically resend it. Check progress or restart the relay.");
        }
        catch (ApiException exception)
        {
            ServerDiagnostics.Log("codex-rpc", Microsoft.Extensions.Logging.LogLevel.Warning, $"request_error id={id} method={method} elapsed_ms={stopwatch.ElapsedMilliseconds}", exception);
            throw;
        }
        catch (Exception exception)
        {
            ServerDiagnostics.Log("codex-rpc", Microsoft.Extensions.Logging.LogLevel.Error, $"request_exception id={id} method={method} elapsed_ms={stopwatch.ElapsedMilliseconds}", exception);
            throw;
        }
        finally
        {
            pending.TryRemove(id, out _);
        }
    }

    public Task Respond(JsonElement id, object result) => Send(new { id, result });

    public Task<JsonElement> WaitForTurnCompletion(string threadId, TimeSpan timeout)
    {
        var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!turnCompletions.TryAdd(threadId, completion))
            throw new InvalidOperationException("Another provider verification is already running.");
        return AwaitTurnCompletion(threadId, completion, timeout);
    }

    private async Task<JsonElement> AwaitTurnCompletion(string threadId, TaskCompletionSource<JsonElement> completion, TimeSpan timeout)
    {
        try { return await completion.Task.WaitAsync(timeout, lifetime.Token); }
        finally { turnCompletions.TryRemove(threadId, out _); }
    }

    public void CancelTurnCompletion(string threadId) => turnCompletions.TryRemove(threadId, out _);

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
                {
                    if (message.Text("method") == "turn/completed" && message.Field("params").Text("threadId") is { Length: > 0 } threadId &&
                        turnCompletions.TryRemove(threadId, out var turnCompletion))
                        turnCompletion.TrySetResult(message.Clone());
                    await events.Writer.WriteAsync(message.Clone(), lifetime.Token);
                }
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            ServerDiagnostics.Log("codex", Microsoft.Extensions.Logging.LogLevel.Error, "output_read_failed", exception);
        }
        finally
        {
            ServerDiagnostics.Log("codex", lifetime.IsCancellationRequested ? Microsoft.Extensions.Logging.LogLevel.Information : Microsoft.Extensions.Logging.LogLevel.Warning,
                $"process_disconnected exited={process?.HasExited == true} reason={(lifetime.IsCancellationRequested ? "shutdown" : "unexpected")}");
            failed = true;
            ready = false;
            foreach (var completion in pending.Values)
                completion.TrySetException(new ApiException(503, "Codex disconnected before confirming the request. Its outcome may be unknown."));
            foreach (var completion in turnCompletions.Values)
                completion.TrySetException(new ApiException(503, "Codex disconnected before completing the verification request."));
            events.Writer.TryWrite(JsonSerializer.SerializeToElement(new { method = "pocketagent/disconnected" }));
        }
    }

    private static string Fingerprint(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..12];

    private static string LogValue(string value) => value.Replace('\r', ' ').Replace('\n', ' ').Replace('"', '\'');

    private async Task DrainErrors()
    {
        // Preserve provider diagnostics for troubleshooting. Provider output must not contain secrets.
        try
        {
            while (await process!.StandardError.ReadLineAsync(lifetime.Token) is { } line)
                ServerDiagnostics.Log("codex-stderr", Microsoft.Extensions.Logging.LogLevel.Warning, $"line={line}");
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
