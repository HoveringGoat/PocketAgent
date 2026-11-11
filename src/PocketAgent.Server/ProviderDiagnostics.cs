using System.Text.Json;

namespace PocketAgent;

public sealed record ModelOption(string Model, string DisplayName, bool IsDefault);
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
                    models.Add(new(slug, model.Text("displayName", slug), model.Field("isDefault").ValueKind == JsonValueKind.True));
            }
            cursor = page.Text("nextCursor") is { Length: > 0 } next ? next : null;
            if (cursor is not null && !cursors.Add(cursor))
                throw new ApiException(502, "Codex returned a repeated model catalog cursor.");
        } while (cursor is not null);

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
            models.DistinctBy(model => model.Model).ToArray(),
            false,
            DateTimeOffset.UtcNow);
    }
}

public static class BuildInfo
{
    public static string Version => typeof(BuildInfo).Assembly.GetName().Version!.ToString(3);
}
