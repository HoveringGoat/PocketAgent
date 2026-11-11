using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;

namespace PocketAgent;

public sealed record ConnectivityReport(bool LocalConnectionVerified, string Address, string[] Checks);

public sealed class ConnectivityDiagnostics(HostSettings settings)
{
    public async Task<ConnectivityReport> Check()
    {
        var checks = new List<string>();
        var address = settings.ClientOrigin;
        var direct = settings.DirectOrigin.Length > 0;
        checks.Add($"Listener: {settings.ListenAddress}:{settings.Port}. Phone address: {address}.");
        if (IPAddress.IsLoopback(IPAddress.Parse(settings.ListenAddress))) checks.Add("Listener is desktop-only. A router forward cannot reach a loopback listener.");
        if (!direct) { checks.Add("Direct internet is not configured. Use Set up direct internet while stopped."); return new(false, address, checks.ToArray()); }
        var origin = new Uri(settings.DirectOrigin);
        checks.Add($"Router: forward external TCP {origin.Port} to {settings.ListenAddress} TCP {settings.Port}. Reserve the desktop LAN address.");
        checks.Add($"Windows firewall: allow inbound TCP {settings.Port} for this desktop on the appropriate network profile. This diagnostic does not change or certify firewall/router rules.");
        checks.Add("WAN: use the router's public IPv4 address. Double NAT needs configuration at both routers; carrier-grade NAT requires a public address from the ISP.");
        var verified = false;
        try
        {
            var authorityPath = Path.Combine(Path.GetDirectoryName(settings.CertificatePath)!, "pocketagent-root.cer");
            using var authority = settings.AutomaticCertificate ? null : X509CertificateLoader.LoadCertificateFromFile(authorityPath);
            using var certificate = X509CertificateLoader.LoadPkcs12FromFile(settings.CertificatePath, null, X509KeyStorageFlags.EphemeralKeySet);
            checks.Add($"Certificate expires {certificate.NotAfter.ToUniversalTime():u}. " + (authority is null ? "Public trust: no phone certificate installation required." : $"Public CA SHA-256: {authority.GetCertHashString(System.Security.Cryptography.HashAlgorithmName.SHA256)}."));
            var policy = new X509ChainPolicy { TrustMode = authority is null ? X509ChainTrustMode.System : X509ChainTrustMode.CustomRootTrust, RevocationMode = X509RevocationMode.NoCheck, DisableCertificateDownloads = true };
            if (authority is not null) policy.CustomTrustStore.Add(authority);
            using var handler = new SocketsHttpHandler
            {
                UseProxy = false,
                SslOptions = new SslClientAuthenticationOptions { CertificateChainPolicy = policy },
                ConnectCallback = async (_, token) =>
                {
                    var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                    try { await socket.ConnectAsync(new IPEndPoint(IPAddress.Parse(settings.ListenAddress), settings.Port), token); return new NetworkStream(socket, ownsSocket: true); }
                    catch { socket.Dispose(); throw; }
                }
            };
            using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) };
            // Connect locally, but verify TLS and the exact Host for the advertised public address. No TLS bypass or internet probe.
            using var response = await client.GetAsync(origin + "api/session");
            var session = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            using (session) verified = response.IsSuccessStatusCode && session.RootElement.TryGetProperty("authenticated", out _) && session.RootElement.TryGetProperty("version", out _);
            checks.Add(verified ? "PASS: local listener, HTTPS certificate chain, public IP certificate identity, exact Host and PocketAgent API respond correctly." : "FAIL: the local API did not return the expected PocketAgent metadata.");
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or IOException or System.Security.Cryptography.CryptographicException or JsonException)
        { checks.Add("FAIL: local HTTPS verification or connection failed. Check the server is running, the certificate matches the public IP, the public CA file exists, and the listener is correct."); }
        checks.Add(settings.AutomaticCertificate ? $"Automatic renewal: forward router TCP 80 to {settings.ListenAddress}:{settings.AcmePort} and allow inbound TCP {settings.AcmePort}. The challenge listener runs only during renewal. Keep the server running to renew before expiry. No phone certificate installation needed." : "Install only the public CA certificate on the phone. Do not copy server.pfx.");
        checks.Add("External access is NOT verified by a local test. Turn Wi-Fi off and use Test connection over cellular to verify the actual public route.");
        checks.Add("No domain, central PocketAgent host or traffic relay is required. If the public IP changes, rerun direct setup and update the phone address/trust; existing tokens are never forwarded to a different origin.");
        return new(verified, address, checks.ToArray());
    }
}
