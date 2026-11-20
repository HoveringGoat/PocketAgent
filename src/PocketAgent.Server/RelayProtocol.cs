using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;

namespace PocketAgent;

public static class RelayProtocol
{
    public const int Version = 1;
    public const int ApplicationVersion = 1;
}

public sealed record RelayEnrollment(string RelayUrl, Guid HostId, string HostToken, string SessionKey, string CertificatePin = "")
{
    public static RelayEnrollment Create(Uri relayUrl)
    {
        var hostId = Guid.NewGuid();
        var token = CreateToken();
        return new(ValidateEndpoint(relayUrl.AbsoluteUri, "").AbsoluteUri, hostId, token, RelaySessionKey.Create().ToWire());
    }

    public static string CreateToken() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();

    public string ToPairingString() =>
        $"pocketagent://relay?v=1&origin={Uri.EscapeDataString(RelayUrl)}&host={HostId}&token={Uri.EscapeDataString(HostToken)}&key={Uri.EscapeDataString(SessionKey)}" +
        (CertificatePin.Length > 0 ? $"&spki={CertificatePin}" : "");

    public static RelayEnrollment Parse(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != "pocketagent" || uri.Host != "relay")
            throw new FormatException("Relay enrollment must be a pocketagent://relay pairing string.");
        var query = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(uri.Query);
        var pin = query.GetValueOrDefault("spki").ToString();
        var token = query.GetValueOrDefault("token").ToString();
        var sessionKey = query.GetValueOrDefault("key").ToString();
        if (query.Count != (query.ContainsKey("spki") ? 6 : 5) || query.Values.Any(value => value.Count != 1) ||
            query.GetValueOrDefault("v") != "1" || !Guid.TryParse(query.GetValueOrDefault("host"), out var hostId) || hostId == Guid.Empty ||
            string.IsNullOrWhiteSpace(token) || string.IsNullOrWhiteSpace(sessionKey))
            throw new FormatException("Relay enrollment is missing required fields.");
        var origin = ValidateEndpoint(query.GetValueOrDefault("origin").ToString(), pin);
        _ = RelaySessionKey.Parse(sessionKey);
        return new(origin.AbsoluteUri, hostId, token, sessionKey, pin);
    }

    public static Uri ValidateEndpoint(string value, string certificatePin)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var origin) || origin.Scheme is not ("ws" or "wss") ||
            origin.Host.Length == 0 || origin.Port == 0 || origin.UserInfo.Length > 0 ||
            origin.AbsolutePath is not ("/" or "/relay") || origin.Query.Length > 0 || origin.Fragment.Length > 0)
            throw new FormatException("Use a relay WebSocket origin or its /relay endpoint without credentials, a query, or a fragment.");
        if (origin.Scheme == "ws" && !origin.IsLoopback)
            throw new FormatException("Use wss:// for a network relay. ws:// is supported on loopback.");
        if (certificatePin.Length > 0 && (origin.Scheme != "wss" || certificatePin.Length != 64 || !certificatePin.All(Uri.IsHexDigit)))
            throw new FormatException("A relay certificate pin must be a 64-digit hexadecimal SHA-256 value for a wss:// endpoint.");
        return new UriBuilder(origin) { Path = "/relay" }.Uri;
    }
}

public sealed record RelayApplicationRequest(
    int Version,
    string Method,
    string Path,
    byte[] Body,
    IReadOnlyList<KeyValuePair<string, string>> Headers)
{
    public static RelayApplicationRequest Create(string method, string path, byte[] body) =>
        new(RelayProtocol.ApplicationVersion, method, path, body, []);
}

public sealed record RelayApplicationResponse(int Version, int Status, string ContentType, byte[] Body)
{
    public static RelayApplicationResponse Create(int status, string contentType, byte[] body) =>
        new(RelayProtocol.ApplicationVersion, status, contentType, body);
}

public sealed record RelayOpaqueEnvelope(string KeyId, byte[] Nonce, byte[] Ciphertext);
public sealed record RelayRequest(Guid RequestId, Guid TargetHost, Guid DeviceId, RelayOpaqueEnvelope Envelope);
public sealed record RelayResponse(Guid RequestId, Guid TargetHost, RelayOpaqueEnvelope Envelope);

public sealed record RelayFrameEnvelope(
    int Version,
    string Kind,
    RelayRequest? Request = null,
    RelayResponse? Response = null)
{
    public static RelayFrameEnvelope ClientRequest(RelayRequest request) =>
        new(RelayProtocol.Version, "client-request", request, null);

    public static RelayFrameEnvelope HostResponse(RelayResponse response) =>
        new(RelayProtocol.Version, "host-response", null, response);
}

internal sealed record RelayWireMessage(
    string Kind,
    Guid? HostId = null,
    Guid? ClientId = null,
    string? Token = null,
    Guid? SessionId = null,
    string? Reason = null,
    RelayFrameEnvelope? Frame = null);

public sealed class RelaySessionKey
{
    private const int KeyBytes = 32;
    private readonly byte[] bytes;

    private RelaySessionKey(Guid id, byte[] bytes)
    {
        Id = id;
        this.bytes = bytes;
    }

    public Guid Id { get; }

    public static RelaySessionKey Create() => new(Guid.NewGuid(), RandomNumberGenerator.GetBytes(KeyBytes));

    public static RelaySessionKey Parse(string value)
    {
        var parts = value.Trim().Split(':', 2);
        if (parts.Length != 2 || !Guid.TryParse(parts[0], out var id) || parts[1].Length != KeyBytes * 2)
            throw new FormatException("Relay session keys must use the id:64-hex format.");
        byte[] key;
        try { key = Convert.FromHexString(parts[1]); }
        catch (FormatException exception) { throw new FormatException("Relay session key is not valid hex.", exception); }
        if (key.Length != KeyBytes) throw new FormatException("Relay session key must contain 32 bytes.");
        return new RelaySessionKey(id, key);
    }

    public string ToWire() => $"{Id}:{Convert.ToHexString(bytes).ToLowerInvariant()}";

    internal byte[] CopyBytes() => bytes.ToArray();
}

public static class RelayCrypto
{
    private const int NonceBytes = 12;
    private const int TagBytes = 16;

    public static RelayOpaqueEnvelope Seal(RelaySessionKey key, Guid hostId, Guid deviceId, Guid requestId, byte[] plaintext)
    {
        var nonce = RandomNumberGenerator.GetBytes(NonceBytes);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[TagBytes];
        using var cipher = new AesGcm(key.CopyBytes(), TagBytes);
        cipher.Encrypt(nonce, plaintext, ciphertext, tag, AssociatedData(hostId, deviceId, requestId));
        return new(key.Id.ToString(), nonce, [.. ciphertext, .. tag]);
    }

    public static byte[] Open(RelaySessionKey key, Guid hostId, Guid deviceId, Guid requestId, RelayOpaqueEnvelope envelope)
    {
        if (!Guid.TryParse(envelope.KeyId, out var keyId) || keyId != key.Id || envelope.Nonce.Length != NonceBytes)
            throw new CryptographicException("The relay envelope belongs to a different session key.");
        if (envelope.Ciphertext.Length < TagBytes)
            throw new CryptographicException("The relay envelope is truncated.");
        var ciphertext = envelope.Ciphertext[..^TagBytes];
        var tag = envelope.Ciphertext[^TagBytes..];
        var plaintext = new byte[ciphertext.Length];
        using var cipher = new AesGcm(key.CopyBytes(), TagBytes);
        cipher.Decrypt(envelope.Nonce, ciphertext, tag, plaintext, AssociatedData(hostId, deviceId, requestId));
        return plaintext;
    }

    private static byte[] AssociatedData(Guid hostId, Guid deviceId, Guid requestId) =>
        Encoding.UTF8.GetBytes($"pocketagent/application/{hostId}/{deviceId}/{requestId}");
}

public static class RelayApplicationCodec
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static RelayRequest EncodeRequest(RelaySessionKey key, Guid hostId, Guid deviceId, Guid requestId, RelayApplicationRequest request)
    {
        if (request.Version != RelayProtocol.ApplicationVersion) throw new InvalidOperationException("Unsupported relay application version.");
        var body = JsonSerializer.SerializeToUtf8Bytes(request, Json);
        return new(requestId, hostId, deviceId, RelayCrypto.Seal(key, hostId, deviceId, requestId, body));
    }

    public static RelayApplicationRequest DecodeRequest(RelaySessionKey key, Guid hostId, RelayRequest request)
    {
        if (request.TargetHost != hostId) throw new InvalidOperationException("Relay request route does not match the host.");
        var body = RelayCrypto.Open(key, hostId, request.DeviceId, request.RequestId, request.Envelope);
        var decoded = JsonSerializer.Deserialize<RelayApplicationRequest>(body, Json)
            ?? throw new InvalidOperationException("Relay request body is empty.");
        if (decoded.Version != RelayProtocol.ApplicationVersion) throw new InvalidOperationException("Unsupported relay application version.");
        return decoded;
    }

    public static RelayResponse EncodeResponse(RelaySessionKey key, Guid hostId, Guid deviceId, Guid requestId, RelayApplicationResponse response)
    {
        if (response.Version != RelayProtocol.ApplicationVersion) throw new InvalidOperationException("Unsupported relay application version.");
        var body = JsonSerializer.SerializeToUtf8Bytes(response, Json);
        return new(requestId, hostId, RelayCrypto.Seal(key, hostId, deviceId, requestId, body));
    }

    public static RelayApplicationResponse DecodeResponse(RelaySessionKey key, Guid hostId, Guid deviceId, RelayResponse response)
    {
        if (response.TargetHost != hostId) throw new InvalidOperationException("Relay response route does not match the host.");
        var body = RelayCrypto.Open(key, hostId, deviceId, response.RequestId, response.Envelope);
        var decoded = JsonSerializer.Deserialize<RelayApplicationResponse>(body, Json)
            ?? throw new InvalidOperationException("Relay response body is empty.");
        if (decoded.Version != RelayProtocol.ApplicationVersion) throw new InvalidOperationException("Unsupported relay application version.");
        return decoded;
    }
}

public sealed class RelayNode
{
    private sealed record Host(Guid SessionId, ChannelWriter<RelayFrameEnvelope> Outbound);
    private sealed record Client(Guid SessionId, ChannelWriter<RelayFrameEnvelope> Outbound);
    private sealed record Pending(Guid HostId, Guid ClientId);
    private readonly ConcurrentDictionary<Guid, Host> hosts = new();
    private readonly ConcurrentDictionary<Guid, Client> clients = new();
    private readonly ConcurrentDictionary<Guid, Pending> pending = new();

    public RelayNodeSession AttachHost(Guid hostId, ChannelWriter<RelayFrameEnvelope> outbound)
    {
        var session = new RelayNodeSession(hostId, Guid.NewGuid(), true);
        hosts[hostId] = new(session.SessionId, outbound);
        return session;
    }

    public RelayNodeSession AttachClient(Guid clientId, ChannelWriter<RelayFrameEnvelope> outbound)
    {
        var session = new RelayNodeSession(clientId, Guid.NewGuid(), false);
        clients[clientId] = new(session.SessionId, outbound);
        return session;
    }

    public async Task RouteRequestAsync(RelayNodeSession session, RelayRequest request, CancellationToken cancellationToken)
    {
        if (session.IsHost || request.DeviceId != session.Identity) throw new InvalidOperationException("Relay client route does not match its session.");
        if (!clients.TryGetValue(session.Identity, out var client) || client.SessionId != session.SessionId) throw new InvalidOperationException("Relay client is not connected.");
        if (!hosts.TryGetValue(request.TargetHost, out var host)) throw new InvalidOperationException("Relay host is not connected.");
        if (!pending.TryAdd(request.RequestId, new(request.TargetHost, session.Identity))) throw new InvalidOperationException("Relay request ID is already pending.");
        try { await host.Outbound.WriteAsync(RelayFrameEnvelope.ClientRequest(request), cancellationToken); }
        catch { pending.TryRemove(request.RequestId, out _); throw; }
    }

    public async Task RouteResponseAsync(RelayNodeSession session, RelayResponse response, CancellationToken cancellationToken)
    {
        if (!session.IsHost) throw new InvalidOperationException("Relay host route does not match its session.");
        if (!hosts.TryGetValue(session.Identity, out var host) || host.SessionId != session.SessionId) throw new InvalidOperationException("Relay host is not connected.");
        if (!pending.TryGetValue(response.RequestId, out var route) || route.HostId != session.Identity || response.TargetHost != session.Identity) throw new InvalidOperationException("Relay response route is not pending.");
        if (!clients.TryGetValue(route.ClientId, out var client)) throw new InvalidOperationException("Relay client is not connected.");
        pending.TryRemove(response.RequestId, out _);
        await client.Outbound.WriteAsync(RelayFrameEnvelope.HostResponse(response), cancellationToken);
    }

    public void Detach(RelayNodeSession session)
    {
        if (session.IsHost)
        {
            if (hosts.TryGetValue(session.Identity, out var host) && host.SessionId == session.SessionId) hosts.TryRemove(session.Identity, out _);
            foreach (var pair in pending.Where(pair => pair.Value.HostId == session.Identity)) pending.TryRemove(pair.Key, out _);
        }
        else
        {
            if (clients.TryGetValue(session.Identity, out var client) && client.SessionId == session.SessionId) clients.TryRemove(session.Identity, out _);
            foreach (var pair in pending.Where(pair => pair.Value.ClientId == session.Identity)) pending.TryRemove(pair.Key, out _);
        }
    }
}

public sealed record RelayNodeSession(Guid Identity, Guid SessionId, bool IsHost);

public sealed class RelayAuthRegistry
{
    private readonly ConcurrentDictionary<Guid, byte[]> hosts = new();
    private readonly ConcurrentDictionary<Guid, byte[]> clients = new();

    public void RegisterHost(Guid id, string token) => hosts[id] = Encoding.UTF8.GetBytes(token);
    public void RegisterClient(Guid id, string token) => clients[id] = Encoding.UTF8.GetBytes(token);

    internal bool VerifyHost(Guid id, string token) => Verify(hosts, id, token);
    internal bool VerifyClient(Guid id, string token) => Verify(clients, id, token);

    private static bool Verify(ConcurrentDictionary<Guid, byte[]> registry, Guid id, string token) =>
        registry.TryGetValue(id, out var expected) && CryptographicOperations.FixedTimeEquals(expected, Encoding.UTF8.GetBytes(token));
}

public sealed class RelayGateway
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly RelayNode node;
    private readonly RelayAuthRegistry auth;
    private readonly IHostApplicationLifetime lifetime;

    public RelayGateway(RelayNode node, RelayAuthRegistry auth, IHostApplicationLifetime lifetime)
    {
        this.node = node;
        this.auth = auth;
        this.lifetime = lifetime;
    }

    public async Task HandleAsync(HttpContext context)
    {
        if (!context.WebSockets.IsWebSocketRequest) { context.Response.StatusCode = StatusCodes.Status400BadRequest; return; }
        using var socket = await context.WebSockets.AcceptWebSocketAsync();
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, lifetime.ApplicationStopping);
        linkedCancellation.CancelAfter(TimeSpan.FromSeconds(15));
        var cancellation = linkedCancellation.Token;
        var first = await ReceiveAsync(socket, cancellation);
        if (first is null) return;
        var authentication = JsonSerializer.Deserialize<RelayWireMessage>(first, Json) ?? throw new InvalidOperationException("Relay authentication was invalid.");
        RelayNodeSession session;
        var outbound = Channel.CreateBounded<RelayFrameEnvelope>(32);
        if (authentication.Kind == "authenticate-host" && authentication.HostId is Guid hostId && authentication.Token is not null && auth.VerifyHost(hostId, authentication.Token))
            session = node.AttachHost(hostId, outbound.Writer);
        else if (authentication.Kind == "authenticate-client" && authentication.ClientId is Guid clientId && authentication.Token is not null && auth.VerifyClient(clientId, authentication.Token))
            session = node.AttachClient(clientId, outbound.Writer);
        else
        {
            await SendAsync(socket, new RelayWireMessage("rejected", Reason: "relay authentication failed"), cancellation);
            return;
        }
        try
        {
            await SendAsync(socket, new RelayWireMessage("authenticated", SessionId: session.SessionId), cancellation);
            linkedCancellation.CancelAfter(Timeout.InfiniteTimeSpan);
            var receiveLoop = ReceiveLoopAsync(socket, session, cancellation);
            var sendLoop = SendLoopAsync(socket, outbound.Reader, cancellation);
            await Task.WhenAny(receiveLoop, sendLoop);
            linkedCancellation.Cancel();
            try { await Task.WhenAll(receiveLoop, sendLoop); } catch (OperationCanceledException) { }
        }
        catch (OperationCanceledException) { }
        finally { linkedCancellation.Cancel(); node.Detach(session); outbound.Writer.TryComplete(); }
    }

    private async Task ReceiveLoopAsync(WebSocket socket, RelayNodeSession session, CancellationToken cancellationToken)
    {
        while (socket.State == WebSocketState.Open)
        {
            var text = await ReceiveAsync(socket, cancellationToken);
            if (text is null) return;
            var wire = JsonSerializer.Deserialize<RelayWireMessage>(text, Json) ?? throw new InvalidOperationException("Relay message was invalid.");
            if (wire.Kind != "frame" || wire.Frame is null || wire.Frame.Version != RelayProtocol.Version) throw new InvalidOperationException("Relay frame version or shape is unsupported.");
            if (session.IsHost && wire.Frame.Kind == "host-response" && wire.Frame.Response is not null) await node.RouteResponseAsync(session, wire.Frame.Response, cancellationToken);
            else if (!session.IsHost && wire.Frame.Kind == "client-request" && wire.Frame.Request is not null) await node.RouteRequestAsync(session, wire.Frame.Request, cancellationToken);
            else throw new InvalidOperationException("Relay frame role is invalid.");
        }
    }

    private static async Task SendLoopAsync(WebSocket socket, ChannelReader<RelayFrameEnvelope> outbound, CancellationToken cancellationToken)
    {
        await foreach (var frame in outbound.ReadAllAsync(cancellationToken))
            await SendAsync(socket, new RelayWireMessage("frame", Frame: frame), cancellationToken);
    }

    private static async Task SendAsync(WebSocket socket, RelayWireMessage message, CancellationToken cancellationToken)
    {
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(message, Json));
        await socket.SendAsync(bytes, WebSocketMessageType.Text, true, cancellationToken);
    }

    private static async Task<string?> ReceiveAsync(WebSocket socket, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        var bytes = new byte[16 * 1024];
        WebSocketReceiveResult result;
        do
        {
            result = await socket.ReceiveAsync(bytes, cancellationToken);
            if (result.MessageType == WebSocketMessageType.Close) return null;
            if (result.MessageType != WebSocketMessageType.Text) throw new InvalidOperationException("Relay messages must be text JSON.");
            buffer.Write(bytes, 0, result.Count);
            if (buffer.Length > 2 * 1024 * 1024) throw new InvalidOperationException("Relay message exceeds the size limit.");
        } while (!result.EndOfMessage);
        return Encoding.UTF8.GetString(buffer.ToArray());
    }
}

public sealed class RelayTransportClient : IAsyncDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly ClientWebSocket socket;
    private RelayTransportClient(ClientWebSocket socket, Guid sessionId) { this.socket = socket; SessionId = sessionId; }
    public Guid SessionId { get; }

    public static async Task<RelayTransportClient> ConnectAsync(Uri uri, bool host, Guid identity, string token, CancellationToken cancellationToken = default, string certificatePin = "")
    {
        var socket = new ClientWebSocket();
        try
        {
            uri = RelayEnrollment.ValidateEndpoint(uri.AbsoluteUri, certificatePin);
            if (certificatePin.Length > 0)
                socket.Options.RemoteCertificateValidationCallback = (_, certificate, _, _) =>
                    certificate is X509Certificate2 presented && MatchesCertificate(presented, uri.IdnHost, certificatePin);
            await socket.ConnectAsync(uri, cancellationToken);
            var message = host ? new RelayWireMessage("authenticate-host", HostId: identity, Token: token) : new RelayWireMessage("authenticate-client", ClientId: identity, Token: token);
            await SendAsync(socket, message, cancellationToken);
            var response = await ReceiveAsync(socket, cancellationToken) ?? throw new InvalidOperationException("Relay closed during authentication.");
            var authenticated = JsonSerializer.Deserialize<RelayWireMessage>(response, Json);
            if (authenticated?.Kind != "authenticated" || authenticated.SessionId is not Guid sessionId)
                throw new InvalidOperationException("Relay authentication failed.");
            return new(socket, sessionId);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    internal static bool MatchesCertificate(X509Certificate2 certificate, string host, string pin)
    {
        var now = DateTime.Now;
        return certificate.NotBefore <= now && certificate.NotAfter >= now &&
            certificate.MatchesHostname(host, allowWildcards: true, allowCommonName: false) &&
            string.Equals(PeerCertificates.Fingerprint(certificate), pin, StringComparison.OrdinalIgnoreCase);
    }

    public async Task SendFrameAsync(RelayFrameEnvelope frame, CancellationToken cancellationToken = default) => await SendAsync(socket, new RelayWireMessage("frame", Frame: frame), cancellationToken);

    public async Task<RelayFrameEnvelope> ReceiveFrameAsync(CancellationToken cancellationToken = default)
    {
        var text = await ReceiveAsync(socket, cancellationToken) ?? throw new InvalidOperationException("Relay closed the connection.");
        var message = JsonSerializer.Deserialize<RelayWireMessage>(text, Json) ?? throw new InvalidOperationException("Relay response was invalid.");
        if (message.Kind != "frame" || message.Frame is null || message.Frame.Version != RelayProtocol.Version) throw new InvalidOperationException("Relay frame was invalid.");
        return message.Frame;
    }

    private static async Task SendAsync(ClientWebSocket socket, RelayWireMessage message, CancellationToken cancellationToken)
    {
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(message, Json));
        await socket.SendAsync(bytes, WebSocketMessageType.Text, true, cancellationToken);
    }

    private static async Task<string?> ReceiveAsync(ClientWebSocket socket, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        var bytes = new byte[16 * 1024];
        WebSocketReceiveResult result;
        do
        {
            result = await socket.ReceiveAsync(bytes, cancellationToken);
            if (result.MessageType == WebSocketMessageType.Close) return null;
            buffer.Write(bytes, 0, result.Count);
        } while (!result.EndOfMessage);
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    public async ValueTask DisposeAsync()
    {
        if (socket.State == WebSocketState.Open)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            try { await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", timeout.Token); }
            catch (Exception exception) when (exception is WebSocketException or OperationCanceledException) { socket.Abort(); }
        }
        socket.Dispose();
    }
}

public sealed class RelayApplicationHost : IAsyncDisposable
{
    private readonly RelayTransportClient transport;
    private readonly RelaySessionKey key;
    private readonly Guid hostId;
    private readonly Func<RelayApplicationRequest, Task<RelayApplicationResponse>> handler;

    private RelayApplicationHost(RelayTransportClient transport, RelaySessionKey key, Guid hostId, Func<RelayApplicationRequest, Task<RelayApplicationResponse>> handler)
    { this.transport = transport; this.key = key; this.hostId = hostId; this.handler = handler; }

    public static async Task<RelayApplicationHost> ConnectAsync(Uri uri, Guid hostId, string token, RelaySessionKey key, Func<RelayApplicationRequest, Task<RelayApplicationResponse>> handler, CancellationToken cancellationToken = default, string certificatePin = "") =>
        new(await RelayTransportClient.ConnectAsync(uri, true, hostId, token, cancellationToken, certificatePin), key, hostId, handler);

    public async Task ServeAsync(CancellationToken cancellationToken = default)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var frame = await transport.ReceiveFrameAsync(cancellationToken);
            if (frame.Kind != "client-request" || frame.Request is null) continue;
            var request = RelayApplicationCodec.DecodeRequest(key, hostId, frame.Request);
            var response = await handler(request);
            var wire = RelayApplicationCodec.EncodeResponse(key, hostId, frame.Request.DeviceId, frame.Request.RequestId, response);
            await transport.SendFrameAsync(RelayFrameEnvelope.HostResponse(wire), cancellationToken);
        }
    }

    public ValueTask DisposeAsync() => transport.DisposeAsync();
}

public sealed class RelayApplicationClient : IAsyncDisposable
{
    private readonly RelayTransportClient transport;
    private readonly RelaySessionKey key;
    private readonly Guid hostId;
    private readonly Guid deviceId;
    private readonly SemaphoreSlim requestGate = new(1, 1);

    private RelayApplicationClient(RelayTransportClient transport, RelaySessionKey key, Guid hostId, Guid deviceId)
    { this.transport = transport; this.key = key; this.hostId = hostId; this.deviceId = deviceId; }

    public static async Task<RelayApplicationClient> ConnectAsync(Uri uri, Guid hostId, Guid deviceId, string token, RelaySessionKey key, CancellationToken cancellationToken = default, string certificatePin = "") =>
        new(await RelayTransportClient.ConnectAsync(uri, false, deviceId, token, cancellationToken, certificatePin), key, hostId, deviceId);

    public async Task<RelayApplicationResponse> RequestAsync(RelayApplicationRequest request, CancellationToken cancellationToken = default)
    {
        await requestGate.WaitAsync(cancellationToken);
        try
        {
            var requestId = Guid.NewGuid();
            var wire = RelayApplicationCodec.EncodeRequest(key, hostId, deviceId, requestId, request);
            await transport.SendFrameAsync(RelayFrameEnvelope.ClientRequest(wire), cancellationToken);
            while (true)
            {
                var frame = await transport.ReceiveFrameAsync(cancellationToken);
                if (frame.Kind == "host-response" && frame.Response?.RequestId == requestId)
                    return RelayApplicationCodec.DecodeResponse(key, hostId, deviceId, frame.Response);
            }
        }
        finally { requestGate.Release(); }
    }

    public ValueTask DisposeAsync() => transport.DisposeAsync();
}
