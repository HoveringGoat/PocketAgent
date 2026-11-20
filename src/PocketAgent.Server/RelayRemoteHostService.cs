using System.Text;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace PocketAgent;

/// <summary>
/// Keeps a remote PocketAgent host's relay connection outbound. The relay sees only
/// encrypted application envelopes; this service is the only component that opens them
/// and forwards the request to the host's local authenticated API.
/// </summary>
public sealed class RelayRemoteHostService(HostSettings settings, PeerCertificates certificates, ILogger<RelayRemoteHostService> logger) : BackgroundService
{
    private volatile RelayConnectionStatus status;
    public RelayConnectionStatus Status => status;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var relay = settings.Relay;
        if (!relay.ConnectsUpstream || string.IsNullOrWhiteSpace(relay.UpstreamUrl)) return;
        var hostId = Guid.Parse(relay.RemoteHostId);
        var key = RelaySessionKey.Parse(relay.SessionKey);
        var uri = new Uri(relay.UpstreamUrl, UriKind.Absolute);
        var handler = new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false, UseProxy = false };
        if (settings.UseHttps)
            handler.ServerCertificateCustomValidationCallback = (_, certificate, _, _) =>
                certificate is not null && RelayTransportClient.MatchesCertificate(certificate, settings.ListenAddress, PeerCertificates.Fingerprint(certificates.Current));
        using var client = new HttpClient(handler) { BaseAddress = new Uri(settings.Origin), Timeout = TimeSpan.FromSeconds(75) };

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                status = RelayConnectionStatus.Connecting;
                using var handshake = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                handshake.CancelAfter(TimeSpan.FromSeconds(15));
                await using var host = await RelayApplicationHost.ConnectAsync(uri, hostId, relay.RemoteHostToken, key,
                    request => DispatchAsync(client, request, stoppingToken), handshake.Token, relay.UpstreamCertificatePin);
                handshake.CancelAfter(Timeout.InfiniteTimeSpan);
                status = RelayConnectionStatus.Connected;
                ServerDiagnostics.Log("relay", LogLevel.Information, $"remote_host_connected host_id={hostId} upstream={uri.Authority}");
                await host.ServeAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Relay remote host connection failed; retrying.");
            }
            if (stoppingToken.IsCancellationRequested) break;
            status = RelayConnectionStatus.Retrying;
            try { await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
        status = RelayConnectionStatus.Stopped;
    }

    private static async Task<RelayApplicationResponse> DispatchAsync(HttpClient client, RelayApplicationRequest request, CancellationToken stoppingToken)
    {
        if (!HttpMethods.IsGet(request.Method) && !HttpMethods.IsHead(request.Method) && !HttpMethods.IsPost(request.Method) && !HttpMethods.IsPut(request.Method) && !HttpMethods.IsDelete(request.Method) && !HttpMethods.IsPatch(request.Method))
            return RelayApplicationResponse.Create(405, "application/json", Encoding.UTF8.GetBytes("{\"error\":\"Unsupported relay method.\"}"));
        if (!Uri.TryCreate(client.BaseAddress, request.Path, out var destination) ||
            destination.GetLeftPart(UriPartial.Authority) != client.BaseAddress!.GetLeftPart(UriPartial.Authority) ||
            !request.Path.StartsWith("/api/", StringComparison.Ordinal) ||
            !destination.AbsolutePath.StartsWith("/api/", StringComparison.Ordinal) || destination.Fragment.Length > 0)
            return RelayApplicationResponse.Create(400, "application/json", Encoding.UTF8.GetBytes("{\"error\":\"Use a local PocketAgent API path.\"}"));
        using var message = new HttpRequestMessage(new HttpMethod(request.Method), destination)
        {
            Content = request.Body.Length == 0 ? null : new ByteArrayContent(request.Body)
        };
        foreach (var header in request.Headers)
        {
            if (header.Key.Equals("Host", StringComparison.OrdinalIgnoreCase) || header.Key.Equals("X-PocketAgent", StringComparison.OrdinalIgnoreCase)) continue;
            if (!message.Headers.TryAddWithoutValidation(header.Key, header.Value) && message.Content is not null)
                message.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }
        message.Headers.TryAddWithoutValidation("X-PocketAgent", "1");
        using var response = await client.SendAsync(message, stoppingToken);
        return RelayApplicationResponse.Create((int)response.StatusCode, response.Content.Headers.ContentType?.ToString() ?? "application/octet-stream", await response.Content.ReadAsByteArrayAsync(stoppingToken));
    }
}

public enum RelayConnectionStatus { Stopped, Connecting, Connected, Retrying }
