using System.Net;
using System.Threading.RateLimiting;

namespace PocketAgent;

// A separate Kestrel host: no application services, files, forwarding or API routes.
public static class AcmeChallengeListener
{
    public static WebApplication Create(IPAddress address, int port, string publicIp, string token, string authorization)
    {
        if (token.Length < 22 || token.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '_' and not '-'))
            throw new InvalidOperationException("Invalid ACME challenge token.");
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args = [], ApplicationName = typeof(AcmeChallengeListener).Assembly.FullName });
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.Limits.MaxConcurrentConnections = 32;
            options.Limits.MaxRequestBodySize = 0;
            options.Limits.MaxRequestHeadersTotalSize = 4096;
            options.Limits.MaxRequestLineSize = 1024;
            options.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(5);
            options.Limits.KeepAliveTimeout = TimeSpan.FromSeconds(5);
            options.Listen(address, port);
        });
        builder.Services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = 429;
            options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(_ =>
                RateLimitPartition.GetFixedWindowLimiter("challenge", _ => new() { PermitLimit = 120, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
        });
        var app = builder.Build();
        app.UseRateLimiter();
        app.Run(async context =>
        {
            context.Response.Headers.CacheControl = "no-store";
            context.Response.Headers.XContentTypeOptions = "nosniff";
            context.Response.Headers.Connection = "close";
            if (context.Request.Method != "GET" || context.Request.Host.Value != publicIp ||
                context.Request.QueryString.HasValue || context.Request.ContentLength is > 0 || context.Request.Headers.ContainsKey("Transfer-Encoding") ||
                context.Request.Path.Value != "/.well-known/acme-challenge/" + token)
            { context.Response.StatusCode = 404; return; }
            context.Response.ContentType = "text/plain";
            await context.Response.WriteAsync(authorization, context.RequestAborted);
        });
        return app;
    }
}
