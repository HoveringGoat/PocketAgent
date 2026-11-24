using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace PocketAgent;

public sealed record ModelOption
{
    public ModelOption(string model, string displayName, bool isDefault, string? defaultReasoningEffort = null, string[]? supportedReasoningEfforts = null)
    {
        Model = model;
        DisplayName = displayName;
        IsDefault = isDefault;
        DefaultReasoningEffort = defaultReasoningEffort;
        SupportedReasoningEfforts = supportedReasoningEfforts ?? [];
    }

    public string Model { get; }
    public string DisplayName { get; }
    public bool IsDefault { get; }
    public string? DefaultReasoningEffort { get; }

    [JsonPropertyName("supportedReasoningEfforts")]
    public string[] SupportedReasoningEfforts { get; }
}

public sealed record ProviderCredentialInfo(string Source, bool Present, string? Fingerprint, string EnvironmentVariable);

public sealed record ProviderStatus(
    bool Connected,
    bool SignInAvailable,
    string? AuthenticationType,
    bool? RequiresOpenaiAuth,
    string? ConfiguredModel,
    string? DefaultModel,
    bool? ConfiguredModelInCatalog,
    ModelOption[] Models,
    bool InferenceVerified,
    DateTimeOffset CheckedAt,
    string? Error = null,
    string? Provider = null,
    bool CredentialPresent = false,
    string? CredentialSource = null,
    string? CredentialEnvironmentVariable = null);

public sealed record InferenceVerification(
    bool Verified,
    string? Model,
    int? ProviderStatus,
    string Message,
    DateTimeOffset CheckedAt);

public sealed class ProviderDiagnostics(CodexConnection codex, HostSettings settings)
{
    public async Task<ProviderStatus> Check()
    {
        try { await codex.EnsureStarted(); }
        catch (ApiException exception)
        {
            var credential = codex.CredentialInfo;
            return new(false, false, null, null, null, null, null, [], false, DateTimeOffset.UtcNow, SafeError(exception.Message), null,
                credential.Present, credential.Source, credential.EnvironmentVariable);
        }

        var account = default(JsonElement);
        var config = default(JsonElement);
        var models = Array.Empty<ModelOption>();
        var failures = new List<string>();
        try { account = await codex.Request("account/read", new { refreshToken = false }); }
        catch (ApiException exception) { failures.Add(SafeError(exception.Message)); }
        try { config = (await codex.Request("config/read", new { includeLayers = false, cwd = settings.RepositoryPath })).Field("config"); }
        catch (ApiException exception) { failures.Add(SafeError(exception.Message)); }
        try { models = await Models(); }
        catch (ApiException exception) { failures.Add(SafeError(exception.Message)); }

        var credentialInfo = codex.CredentialInfo;
        var configuredModel = config.Text("model") is { Length: > 0 } name ? name : null;
        var provider = config.Text("model_provider") is { Length: > 0 } configuredProvider ? configuredProvider : config.Text("modelProvider");
        var accountObject = account.Field("account");
        var accountType = accountObject.Text("type");
        string? authenticationType = accountType.Length > 0 ? accountType : credentialInfo.Present ? "openai-api-key" : null;
        var requiresAuth = account.Field("requiresOpenaiAuth");
        bool? configuredInCatalog = configuredModel is null ? null : models.Any(model => model.Model == configuredModel);
        return new(
            codex.IsReady,
            accountObject.ValueKind == JsonValueKind.Object || credentialInfo.Present,
            authenticationType,
            requiresAuth.ValueKind is JsonValueKind.True or JsonValueKind.False ? requiresAuth.GetBoolean() : null,
            configuredModel,
            models.FirstOrDefault(model => model.IsDefault)?.Model,
            configuredInCatalog,
            models,
            false,
            DateTimeOffset.UtcNow,
            failures.Count == 0 ? null : string.Join(" ", failures.Distinct(StringComparer.Ordinal)),
            provider.Length > 0 ? provider : null,
            credentialInfo.Present,
            credentialInfo.Source,
            credentialInfo.EnvironmentVariable);
    }

    public async Task<InferenceVerification> VerifyInference()
    {
        var checkedAt = DateTimeOffset.UtcNow;
        try
        {
            await codex.EnsureStarted();
            var config = (await codex.Request("config/read", new { includeLayers = false, cwd = settings.RepositoryPath })).Field("config");
            var model = config.Text("model");
            var threadParameters = CodexPermissions.Apply(settings, new { cwd = settings.RepositoryPath });
            if (model.Length > 0) threadParameters["model"] = model;
            var threadResult = await codex.Request("thread/start", threadParameters);
            var threadId = threadResult.Field("thread").Text("id");
            if (threadId.Length == 0) throw new ApiException(502, "Codex did not return a verification thread.");

            var completion = codex.WaitForTurnCompletion(threadId, TimeSpan.FromSeconds(60));
            try
            {
                var turnParameters = CodexPermissions.Apply(settings, new
                {
                    threadId,
                    cwd = settings.RepositoryPath,
                    input = new[] { new { type = "text", text = "pocketagent-inference-check", text_elements = Array.Empty<object>() } }
                }, turn: true, workingDirectory: settings.RepositoryPath);
                if (model.Length > 0) turnParameters["model"] = model;
                var started = await codex.Request("turn/start", turnParameters);
                var turnId = started.Field("turn").Text("id");
                if (turnId.Length == 0) throw new ApiException(502, "Codex did not return a verification turn.");
                var completed = await completion;
                var turn = completed.Field("params").Field("turn");
                var status = turn.Text("status", "failed");
                var error = turn.Field("error").Text("message");
                if (status.Equals("completed", StringComparison.OrdinalIgnoreCase) && error.Length == 0)
                    return new(true, model.Length > 0 ? model : null, null, "Inference access verified.", checkedAt);
                var safeError = SafeError(error.Length > 0 ? error : "Codex did not complete the verification request.");
                return new(false, model.Length > 0 ? model : null, ParseProviderStatus(safeError), safeError, checkedAt);
            }
            finally { codex.CancelTurnCompletion(threadId); }
        }
        catch (Exception exception) when (exception is ApiException or TimeoutException or IOException)
        {
            var message = SafeError(exception.Message);
            return new(false, null, ParseProviderStatus(message), message, checkedAt);
        }
    }

    public async Task<ModelOption[]> Models()
    {
        await codex.EnsureStarted();
        var models = new List<ModelOption>();
        var cursors = new HashSet<string>(StringComparer.Ordinal);
        string? cursor = null;
        do
        {
            var page = await codex.Request("model/list", new { cursor, limit = 100, includeHidden = false });
            var data = page.Field("data");
            if (data.ValueKind != JsonValueKind.Array)
                throw new ApiException(502, "Codex did not return a model catalog.");
            foreach (var model in data.EnumerateArray())
            {
                var slug = model.Text("model");
                if (slug.Length > 0)
                {
                    var supported = model.Field("supportedReasoningEfforts").ValueKind == JsonValueKind.Array
                        ? model.GetProperty("supportedReasoningEfforts").EnumerateArray().Select(option => option.Text("reasoningEffort")).Where(value => value.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToArray()
                        : [];
                    models.Add(new(slug, model.Text("displayName", slug), model.Field("isDefault").ValueKind == JsonValueKind.True, model.Text("defaultReasoningEffort"), supported));
                }
            }
            cursor = page.Text("nextCursor") is { Length: > 0 } next ? next : null;
            if (cursor is not null && !cursors.Add(cursor))
                throw new ApiException(502, "Codex returned a repeated model catalog cursor.");
        } while (cursor is not null);

        return models.DistinctBy(model => model.Model).ToArray();
    }

    private static int? ParseProviderStatus(string message) =>
        Regex.Match(message, @"\b(?<status>[45]\d{2})\b", RegexOptions.CultureInvariant) is { Success: true } match && int.TryParse(match.Groups["status"].Value, out var status) ? status : null;

    private static string SafeError(string message) => Regex.Replace(message.Replace('\r', ' ').Replace('\n', ' '), @"(?i)(authorization|token|password|secret|api[-_]?key)\s*([=:])\s*([^\s&,;]+)", "$1$2<redacted>");
}

public static class BuildInfo
{
    public static string Version => typeof(BuildInfo).Assembly.GetName().Version!.ToString(3);
}
