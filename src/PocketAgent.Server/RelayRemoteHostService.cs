using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace PocketAgent;

/// <summary>
/// Keeps a remote PocketAgent host's relay connection outbound. The relay sees only
/// encrypted application envelopes; this service is the only component that opens them
/// and forwards the request to the host's local authenticated API.
/// </summary>
public sealed class RelayRemoteHostService(HostSettings settings, ILogger<RelayRemoteHostService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var relay = settings.Relay;
        if (string.IsNullOrWhiteSpace(relay.UpstreamUrl)) return;
        if (!Guid.TryParse(relay.RemoteHostId, out var hostId) || string.IsNullOrWhiteSpace(relay.RemoteHostToken))
            throw new InvalidOperationException("Relay.RemoteHostId and Relay.RemoteHostToken are required when Relay.UpstreamUrl is configured.");
        var key = RelaySessionKey.Parse(relay.SessionKey);
        var uri = new Uri(relay.UpstreamUrl, UriKind.Absolute);
        if (uri.Scheme is not ("ws" or "wss")) throw new InvalidOperationException("Relay.UpstreamUrl must use ws:// or wss://.");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var host = await RelayApplicationHost.ConnectAsync(uri, hostId, relay.RemoteHostToken, key, DispatchAsync, stoppingToken);
                ServerDiagnostics.Log("relay", LogLevel.Information, $"remote_host_connected host_id={hostId} upstream={uri.Authority}");
                await host.ServeAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Relay remote host connection failed; retrying.");
                ServerDiagnostics.Log("relay", LogLevel.Warning, $"remote_host_disconnected host_id={hostId}", exception);
                try { await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken); }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            }
        }
    }

    private async Task<RelayApplicationResponse> DispatchAsync(RelayApplicationRequest request)
    {
        if (!HttpMethods.IsGet(request.Method) && !HttpMethods.IsHead(request.Method) && !HttpMethods.IsPost(request.Method) && !HttpMethods.IsPut(request.Method) && !HttpMethods.IsDelete(request.Method) && !HttpMethods.IsPatch(request.Method))
            return RelayApplicationResponse.Create(405, "application/json", Encoding.UTF8.GetBytes("{\"error\":\"Unsupported relay method.\"}"));
        var handler = new HttpClientHandler();
        if (settings.UseHttps) handler.ServerCertificateCustomValidationCallback = (_, certificate, _, _) => certificate is not null && certificate.GetNameInfo(System.Security.Cryptography.X509Certificates.X509NameType.SimpleName, false) == settings.ListenAddress;
        using var client = new HttpClient(handler) { BaseAddress = new Uri(settings.Origin), Timeout = TimeSpan.FromSeconds(75) };
        using var message = new HttpRequestMessage(new HttpMethod(request.Method), request.Path)
        {
            Content = request.Body.Length == 0 ? null : new ByteArrayContent(request.Body)
        };
        foreach (var header in request.Headers)
        {
            if (header.Key.Equals("Host", StringComparison.OrdinalIgnoreCase)) continue;
            if (!message.Headers.TryAddWithoutValidation(header.Key, header.Value) && message.Content is not null)
                message.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }
        message.Headers.TryAddWithoutValidation("X-PocketAgent", "1");
        using var response = await client.SendAsync(message);
        return RelayApplicationResponse.Create((int)response.StatusCode, response.Content.Headers.ContentType?.ToString() ?? "application/octet-stream", await response.Content.ReadAsByteArrayAsync());
    }
}
