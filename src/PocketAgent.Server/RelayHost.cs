using System.Net;
using System.Security.Claims;
using System.Security.Cryptography.X509Certificates;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication;
using PocketAgent;

namespace PocketAgent;

public static class RelayHost
{
    public static WebApplication Create(string[] args, HostSettings? configured = null)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = args, ApplicationName = typeof(RelayHost).Assembly.FullName });
        var settings = configured ?? builder.Configuration.GetSection("PocketAgent").Get<HostSettings>() ?? new();
        settings.Validate();
        ServerDiagnostics.Initialize();
        builder.Logging.ClearProviders();
        builder.Logging.AddProvider(new PocketAgentLoggerProvider());
        var certificates = new PeerCertificates(settings);

        builder.WebHost.ConfigureKestrel(options =>
        {
            options.Limits.MaxRequestBodySize = 128 * 1024;
            options.Listen(IPAddress.Parse(settings.ListenAddress), settings.Port, listener =>
            {
                if (settings.UseHttps)
                {
                    if (settings.PinnedCertificate)
                        listener.UseHttps(certificates.ConfigureHttps);
                    else
                    // Windows Schannel requires a user-backed private key for TLS handshakes.
                    listener.UseHttps(X509CertificateLoader.LoadPkcs12FromFile(settings.CertificatePath, null,
                        OperatingSystem.IsWindows() ? X509KeyStorageFlags.UserKeySet : X509KeyStorageFlags.EphemeralKeySet));
                }
            });
        });
        builder.Services.AddSingleton(settings);
        builder.Services.AddSingleton(certificates);
        builder.Services.AddSingleton<HostLease>();
        builder.Services.AddHostedService(serviceProvider => serviceProvider.GetRequiredService<HostLease>());
        builder.Services.AddHostedService(serviceProvider => serviceProvider.GetRequiredService<PeerCertificates>());
        builder.Services.AddSingleton(serviceProvider =>
        {
            // Service construction can precede hosted-service startup. Acquire ownership before schema migration.
            serviceProvider.GetRequiredService<HostLease>().Acquire();
            return new LocalStore(settings);
        });
        builder.Services.AddSingleton<PairingCode>();
        builder.Services.AddSingleton<CodexConnection>();
        builder.Services.AddSingleton<ProviderDiagnostics>();
        builder.Services.AddSingleton<ProviderUsage>();
        builder.Services.AddSingleton<WorkspaceDirectories>();
        builder.Services.AddSingleton<ConnectivityDiagnostics>();
        builder.Services.AddSingleton<ProgressPublisher>();
        builder.Services.AddSingleton<Conversations>();
        builder.Services.AddHostedService(serviceProvider => serviceProvider.GetRequiredService<Conversations>());
        builder.Services.AddSingleton<ScheduledMessages>();
        builder.Services.AddHostedService(serviceProvider => serviceProvider.GetRequiredService<ScheduledMessages>());
        builder.Services.AddHostedService<ConversationMaintenance>();
        builder.Services.AddHostedService<HeartbeatService>();
        builder.Services.AddSignalR(options => options.MaximumReceiveMessageSize = 16 * 1024);
        builder.Services.AddAuthentication(DeviceAuthentication.SchemeName)
            .AddScheme<AuthenticationSchemeOptions, DeviceAuthentication>(DeviceAuthentication.SchemeName, _ => { });
        builder.Services.AddAuthorization();
        builder.Services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.AddPolicy("pairing", context => RateLimitPartition.GetFixedWindowLimiter(
                context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                _ => new FixedWindowRateLimiterOptions { PermitLimit = 5, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
        });

        var app = builder.Build();
        app.Use(async (context, next) =>
        {
            context.Response.Headers.XContentTypeOptions = "nosniff";
            context.Response.Headers["Referrer-Policy"] = "no-referrer";
            context.Response.Headers.ContentSecurityPolicy = "default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' data:; connect-src 'self'; frame-ancestors 'none'; base-uri 'none'; form-action 'self'";
            var localAuthority = new Uri(settings.Origin).Authority;
            var publicAuthority = settings.DirectOrigin.Length > 0 || settings.PublicOrigin.Length > 0 ? new Uri(settings.ClientOrigin).Authority : null;
            if (!string.Equals(context.Request.Host.Value, localAuthority, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(context.Request.Host.Value, publicAuthority, StringComparison.OrdinalIgnoreCase))
            {
                context.Response.StatusCode = 400;
                return;
            }
            var isHub = context.Request.Path.StartsWithSegments("/events");
            var isMutation = context.Request.Method is not "GET" and not "HEAD" and not "OPTIONS";
            var origin = context.Request.Headers.Origin.ToString();
            var validOrigin = string.Equals(origin, settings.Origin, StringComparison.OrdinalIgnoreCase) ||
                ((settings.DirectOrigin.Length > 0 || settings.PublicOrigin.Length > 0) && string.Equals(origin, settings.ClientOrigin, StringComparison.OrdinalIgnoreCase));
            if ((origin.Length > 0 && !validOrigin) || (isHub && !validOrigin) ||
                (isMutation && !isHub && context.Request.Headers["X-PocketAgent"] != "1"))
            {
                ServerDiagnostics.Log("api", LogLevel.Warning, $"request_rejected method={context.Request.Method} path={context.Request.Path} reason={(origin.Length > 0 && !validOrigin ? "origin" : isHub && !validOrigin ? "hub_origin" : "missing_client_header")}");
                context.Response.StatusCode = 403;
                return;
            }
            if (context.Request.Path.StartsWithSegments("/api"))
                context.Response.Headers.CacheControl = "no-store";
            try { await next(); }
            catch (ApiException exception) when (!context.Response.HasStarted)
            {
                ServerDiagnostics.Log("api", LogLevel.Warning, $"request_failed method={context.Request.Method} path={context.Request.Path} status={exception.Status}", exception);
                context.Response.StatusCode = exception.Status;
                await context.Response.WriteAsJsonAsync(new { error = exception.Message });
            }
            catch (BadHttpRequestException) when (!context.Response.HasStarted)
            {
                context.Response.StatusCode = 400;
                await context.Response.WriteAsJsonAsync(new { error = "Invalid request." });
            }
            catch (Exception exception) when (!context.Response.HasStarted)
            {
                // Do not log request bodies, provider errors, session cookies, or pairing credentials.
                ServerDiagnostics.Log("api", LogLevel.Error, $"request_failed method={context.Request.Method} path={context.Request.Path} status=500", exception);
                context.Response.StatusCode = 500;
                await context.Response.WriteAsJsonAsync(new { error = "The desktop relay could not complete the request. Check local configuration and restart if needed." });
            }
        });
        app.UseAuthentication();
        app.UseAuthorization();
        app.UseRateLimiter();

        app.MapGet("/api/session", (HttpContext context) => Results.Ok(new
        {
            authenticated = context.User.Identity?.IsAuthenticated == true,
            deviceName = context.User.Identity?.Name,
            repository = context.User.Identity?.IsAuthenticated == true ? Path.GetFileName(settings.RepositoryPath) : null,
            hostName = context.User.Identity?.IsAuthenticated == true ? Environment.MachineName : null,
            version = BuildInfo.Version
        }));
        app.MapPost("/api/pair", (PairInput input, HttpContext context, PairingCode code, LocalStore store) =>
        {
            if (string.IsNullOrWhiteSpace(input.DeviceName) || input.DeviceName.Length > 80 || input.Code is null || !code.Consume(input.Code))
            {
                ServerDiagnostics.Log("pairing", LogLevel.Warning, $"legacy_pair_failed name_length={input.DeviceName?.Length ?? 0}");
                return Results.Json(new { error = "The pairing code is invalid, expired, used, or temporarily rate limited. Check the desktop console." }, statusCode: 401);
            }
            var paired = store.Pair(input.DeviceName.Trim());
            ServerDiagnostics.Log("pairing", LogLevel.Information, $"paired device_id={paired.Device.Id} name_length={paired.Device.Name.Length} channel=legacy");
            context.Response.Cookies.Append(DeviceAuthentication.CookieName, paired.Token, new CookieOptions
            {
                HttpOnly = true,
                SameSite = SameSiteMode.Strict,
                Secure = settings.UseHttps,
                Path = "/",
                Expires = paired.Device.ExpiresAt,
                IsEssential = true
            });
            return Results.Ok(new { paired = true });
        }).RequireRateLimiting("pairing");
        app.MapPost("/api/devices/pair", (PairInput input, PairingCode code, LocalStore store) =>
        {
            if (string.IsNullOrWhiteSpace(input.DeviceName) || input.DeviceName.Length > 80 || input.Code is null || !code.Consume(input.Code))
            {
                ServerDiagnostics.Log("pairing", LogLevel.Warning, $"pair_failed name_length={input.DeviceName?.Length ?? 0}");
                return Results.Json(new { error = "Invalid, expired, used, or rate-limited pairing code. Generate another code on the desktop." }, statusCode: 401);
            }
            var paired = store.Pair(input.DeviceName.Trim());
            ServerDiagnostics.Log("pairing", LogLevel.Information, $"paired device_id={paired.Device.Id} name_length={paired.Device.Name.Length} channel=device");
            return Results.Ok(new { token = paired.Token, deviceId = paired.Device.Id, expiresAt = paired.Device.ExpiresAt, deviceName = paired.Device.Name });
        }).RequireRateLimiting("pairing");

        app.Lifetime.ApplicationStarted.Register(() => ServerDiagnostics.Log("relay", LogLevel.Information, $"READY listen={settings.ListenAddress}:{settings.Port} https={settings.UseHttps} data_directory_configured={!string.IsNullOrWhiteSpace(settings.DataDirectory)}"));
        app.Lifetime.ApplicationStopping.Register(() => ServerDiagnostics.Stop("application-stopping"));

        var api = app.MapGroup("/api").RequireAuthorization();
        api.MapGet("/provider/status", (ProviderDiagnostics diagnostics) => diagnostics.Check());
        api.MapGet("/models", (ProviderDiagnostics diagnostics) => diagnostics.Models());
        api.MapGet("/directories", (string? path, WorkspaceDirectories directories) => directories.Browse(path));
        api.MapGet("/usage", (ProviderUsage usage) => usage.Check());
        api.MapGet("/scheduled", (HttpContext context, LocalStore store) => store.Scheduled(deviceId: context.User.FindFirstValue(ClaimTypes.NameIdentifier)));
        api.MapGet("/conversations/{id}/scheduled", (string id, HttpContext context, LocalStore store, Conversations conversations) =>
        {
            var deviceId = context.User.FindFirstValue(ClaimTypes.NameIdentifier);
            conversations.RequireAccess(id, deviceId);
            return store.Scheduled(id);
        });
        api.MapGet("/conversations/{id}/history", async (string id, string? before, int? limit, HttpContext context, Conversations conversations) =>
            await conversations.History(id, before, limit ?? 50, context.User.FindFirstValue(ClaimTypes.NameIdentifier)));
        api.MapGet("/conversations/{id}/images", async (string id, string? path, HttpContext context, Conversations conversations) =>
        {
            if (string.IsNullOrWhiteSpace(path)) throw new ApiException(400, "An image path is required.");
            var image = await conversations.ReadImage(id, path, context.User.FindFirstValue(ClaimTypes.NameIdentifier));
            return Results.File(image.Bytes, image.ContentType, enableRangeProcessing: false);
        });
        api.MapPost("/conversations/{id}/scheduled", (string id, ScheduleInput input, HttpContext context, ScheduledMessages messages) =>
            messages.Schedule(id, input.Text ?? "", input.DueAt, context.User.FindFirstValue(ClaimTypes.NameIdentifier), input.ClientMessageId));
        api.MapPost("/scheduled/{id}/cancel", async (string id, HttpContext context, ScheduledMessages messages) => { await messages.Cancel(id, context.User.FindFirstValue(ClaimTypes.NameIdentifier)); return Results.NoContent(); });
        api.MapPost("/logout", (HttpContext context, LocalStore store) =>
        {
            var deviceId = context.User.FindFirstValue(ClaimTypes.NameIdentifier)!;
            store.Revoke(deviceId);
            ServerDiagnostics.Log("auth", LogLevel.Information, $"device_revoked device_id={deviceId} reason=logout");
            context.Response.Cookies.Delete(DeviceAuthentication.CookieName);
            return Results.NoContent();
        });
        api.MapGet("/conversations", (bool? archived, HttpContext context, Conversations conversations) => conversations.List(archived ?? false, context.User.FindFirstValue(ClaimTypes.NameIdentifier)));
        api.MapPost("/conversations", async (CreateInput input, HttpContext context, Conversations conversations) => Results.Ok(await conversations.Create(input.Title ?? "", input.WorkingDirectory, input.Model, input.ReasoningEffort, context.User.FindFirstValue(ClaimTypes.NameIdentifier))));
        api.MapPost("/conversations/{id}/rename", (string id, CreateInput input, HttpContext context, Conversations conversations) => conversations.Rename(id, input.Title ?? "", context.User.FindFirstValue(ClaimTypes.NameIdentifier)));
        api.MapPost("/conversations/{id}/model", (string id, ModelInput input, HttpContext context, Conversations conversations) => conversations.SetModel(id, input.Model ?? "", context.User.FindFirstValue(ClaimTypes.NameIdentifier)));
        api.MapPost("/conversations/{id}/reasoning", (string id, ReasoningInput input, HttpContext context, Conversations conversations) => conversations.SetReasoning(id, input.Effort, context.User.FindFirstValue(ClaimTypes.NameIdentifier)));
        api.MapPost("/conversations/{id}/pin", (string id, PinInput input, HttpContext context, Conversations conversations) => conversations.SetPinned(id, input.Pinned, context.User.FindFirstValue(ClaimTypes.NameIdentifier)));
        api.MapPost("/conversations/{id}/archive", (string id, HttpContext context, Conversations conversations) => conversations.Archive(id, true, context.User.FindFirstValue(ClaimTypes.NameIdentifier)));
        api.MapPost("/conversations/{id}/restore", (string id, HttpContext context, Conversations conversations) => conversations.Archive(id, false, context.User.FindFirstValue(ClaimTypes.NameIdentifier)));
        api.MapGet("/conversations/{id}", (string id, HttpContext context, Conversations conversations) => conversations.Get(id, context.User.FindFirstValue(ClaimTypes.NameIdentifier)));
        api.MapPost("/conversations/{id}/messages", async (string id, MessageInput input, ScheduledMessages messages, HttpContext context) =>
        {
            var receipt = await messages.Send(id, input.Text ?? "", input.ExpectedTurnId, context.User.FindFirstValue(ClaimTypes.NameIdentifier), input.ClientMessageId);
            return Results.Accepted(value: receipt);
        });
        api.MapPost("/conversations/{id}/interrupt", async (string id, HttpContext context, Conversations conversations) =>
        {
            await conversations.Interrupt(id, context.User.FindFirstValue(ClaimTypes.NameIdentifier));
            return Results.Accepted();
        });
        api.MapPost("/conversations/{id}/requests/{requestId}", async (string id, string requestId, AnswerRequest input, HttpContext context, Conversations conversations) =>
        {
            await conversations.Answer(id, requestId, input, context.User.FindFirstValue(ClaimTypes.NameIdentifier));
            return Results.Accepted();
        });
        app.MapHub<ProgressHub>("/events").RequireAuthorization();
        app.MapGet("/", () => Results.Json(new { application = "PocketAgent", version = BuildInfo.Version, client = "Native Android app" }));
        return app;
    }
}

public sealed record PairInput(string? Code, string? DeviceName);
public sealed record CreateInput(string? Title, string? WorkingDirectory = null, string? Model = null, string? ReasoningEffort = null);
public sealed record ModelInput(string? Model);
public sealed record ReasoningInput(string? Effort);
public sealed record PinInput(bool Pinned);
public sealed record MessageInput(string? Text, string? ExpectedTurnId = null, string? ClientMessageId = null);
public sealed record ScheduleInput(string? Text, DateTimeOffset DueAt, string? ClientMessageId = null);
