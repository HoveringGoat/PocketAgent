using System.Text.Json;

namespace PocketAgent;

public sealed record UsageWindow(int RemainingPercent, DateTimeOffset? ResetsAt, long WindowMinutes);
public sealed record UsageStatus(bool Available, UsageWindow? FiveHour, UsageWindow? Weekly, bool Exhausted, DateTimeOffset? NextResetAt, DateTimeOffset CheckedAt);

public sealed class ProviderUsage(CodexConnection codex)
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private UsageStatus? current;
    public async Task<UsageStatus> Check(bool force = false)
    {
        await gate.WaitAsync();
        try
        {
            var cached = Volatile.Read(ref current);
            if (!force && cached is not null && DateTimeOffset.UtcNow - cached.CheckedAt < TimeSpan.FromSeconds(20)) return cached;
            try
            {
                await codex.EnsureStarted();
                Update(await codex.Request("account/rateLimits/read", new { }));
            }
            catch (ApiException) { Volatile.Write(ref current, new(false, null, null, false, null, DateTimeOffset.UtcNow)); }
            return Volatile.Read(ref current)!;
        }
        finally { gate.Release(); }
    }
    public void Update(JsonElement data)
    {
        // Account IDs, credits, plan information and upsell payloads are never exposed.
        var bucket = data.Field("rateLimitsByLimitId").Field("codex");
        if (bucket.ValueKind != JsonValueKind.Object) bucket = data.Field("rateLimits");
        var windows = new[] { Window(bucket.Field("primary")), Window(bucket.Field("secondary")) }.OfType<UsageWindow>().ToArray();
        var exhausted = windows.Where(window => window.RemainingPercent == 0).ToArray();
        var resets = exhausted.Select(window => window.ResetsAt).OfType<DateTimeOffset>().Where(time => time > DateTimeOffset.UtcNow).ToArray();
        Volatile.Write(ref current, new(windows.Length > 0, windows.FirstOrDefault(window => window.WindowMinutes == 300),
            windows.FirstOrDefault(window => window.WindowMinutes == 10080), exhausted.Length > 0,
            resets.Length > 0 ? resets.Max() : null, DateTimeOffset.UtcNow));
    }
    private static UsageWindow? Window(JsonElement value)
    {
        if (!value.Field("usedPercent").TryGetInt32Safe(out var percent)) return null;
        DateTimeOffset? reset = value.Field("resetsAt").TryGetInt64Safe(out var seconds) && seconds > 0 && seconds < 253402300799 ? DateTimeOffset.FromUnixTimeSeconds(seconds) : null;
        return new(Math.Clamp(100 - percent, 0, 100), reset, value.Field("windowDurationMins").TryGetInt64Safe(out var minutes) ? minutes : 0);
    }
}
