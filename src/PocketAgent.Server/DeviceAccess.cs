using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;

namespace PocketAgent;

public sealed class PairingCode
{
    private readonly object gate = new();
    private string code = NewCode();
    private DateTimeOffset expires = DateTimeOffset.UtcNow.AddMinutes(10);
    private bool consumed;
    private readonly Queue<DateTimeOffset> attempts = new();

    private static string NewCode() => RandomNumberGenerator.GetInt32(100_000, 1_000_000).ToString(System.Globalization.CultureInfo.InvariantCulture);
    public (string Code, DateTimeOffset ExpiresAt, bool Consumed) Current { get { lock (gate) return (code, expires, consumed); } }
    public void Renew()
    {
        lock (gate) { code = NewCode(); expires = DateTimeOffset.UtcNow.AddMinutes(10); consumed = false; }
    }
    public void Display() => Console.WriteLine($"Pairing code: {Current.Code} (single use; expires {Current.ExpiresAt.LocalDateTime:t}).");

    public bool Consume(string candidate)
    {
        lock (gate)
        {
            var now = DateTimeOffset.UtcNow;
            while (attempts.TryPeek(out var attempt) && attempt <= now.AddMinutes(-1))
                attempts.Dequeue();
            if (attempts.Count >= 10)
                return false;
            attempts.Enqueue(now);
            if (consumed || DateTimeOffset.UtcNow >= expires || candidate.Length != code.Length ||
                !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(code), Encoding.UTF8.GetBytes(candidate)))
                return false;
            consumed = true;
            return true;
        }
    }
}

public sealed class DeviceAuthentication(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    LocalStore store) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "Device";
    public const string CookieName = "PocketAgent.Device";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var authorization = Request.Headers.Authorization.ToString();
        var token = authorization.StartsWith("Bearer ", StringComparison.Ordinal) ? authorization[7..] : Request.Cookies[CookieName] ?? "";
        var device = store.FindDevice(token);
        if (device is null)
            return Task.FromResult(AuthenticateResult.NoResult());
        var identity = new ClaimsIdentity([
            new Claim(ClaimTypes.NameIdentifier, device.Id),
            new Claim(ClaimTypes.Name, device.Name)
        ], SchemeName);
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName)));
    }
}

[Authorize]
public sealed class ProgressHub : Hub
{
    public override async Task OnConnectedAsync()
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, Context.UserIdentifier!);
        await base.OnConnectedAsync();
    }
}

public sealed class ProgressPublisher(IHubContext<ProgressHub> hub, LocalStore store)
{
    public Task Changed(string? conversationId = null) => hub.Clients.Groups(store.ActiveDeviceIds()).SendAsync("changed", conversationId);
}

public sealed class ApiException(int status, string message) : Exception(message)
{
    public int Status { get; } = status;
}
