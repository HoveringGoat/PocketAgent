using System.Text.Json;
using System.Text.Json.Serialization;

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
    DateTimeOffset CheckedAt);

public sealed class ProviderDiagnostics(CodexConnection codex, HostSettings settings)
{
    public async Task<ProviderStatus> Check()
    {
        // Use the very same connection as conversation requests. No token refresh or inference.
        await codex.EnsureStarted();
        var accountResult = await codex.Request("account/read", new { refreshToken = false });
        var configResult = await codex.Request("config/read", new { includeLayers = false, cwd = settings.RepositoryPath });
        var configuredModel = configResult.Field("config").Text("model") is { Length: > 0 } name ? name : null;
        var models = await Models();

        var account = accountResult.Field("account");
        var requiresAuth = accountResult.Field("requiresOpenaiAuth");
        // Project only safe fields; account IDs, emails, tokens, and raw configuration stay local.
        return new(
            codex.IsReady,
            account.ValueKind == JsonValueKind.Object,
            account.Text("type") is { Length: > 0 } type ? type : null,
            requiresAuth.ValueKind is JsonValueKind.True or JsonValueKind.False ? requiresAuth.GetBoolean() : null,
            configuredModel,
            models.FirstOrDefault(model => model.IsDefault)?.Model,
            configuredModel is null ? null : models.Any(model => model.Model == configuredModel),
            models,
            false,
            DateTimeOffset.UtcNow);
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
            foreach (var model in page.GetProperty("data").EnumerateArray())
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
}

public static class BuildInfo
{
    public static string Version => typeof(BuildInfo).Assembly.GetName().Version!.ToString(3);
}
