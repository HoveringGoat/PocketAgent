using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;

namespace PocketAgent;

public sealed record ConnectivityReport(bool LocalConnectionVerified, string Address, string[] Checks);

public sealed class ConnectivityDiagnostics(HostSettings settings)
{
    public async Task<ConnectivityReport> Check(Func<int, bool>? firewallRuleChecker = null)
    {
        var checks = new List<string>();
        var address = settings.ClientOrigin;
        var direct = settings.DirectOrigin.Length > 0;
        checks.Add($"Listener: {settings.ListenAddress}:{settings.Port}. Phone address: {address}.");
        var adapter = System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces().FirstOrDefault(network => network.GetIPProperties().UnicastAddresses.Any(item => item.Address.ToString() == settings.ListenAddress));
        if (adapter is not null)
        {
            checks.Add($"Listener adapter: {adapter.Name}.");
            if (!adapter.GetIPProperties().GatewayAddresses.Any(gateway => gateway.Address.AddressFamily == AddressFamily.InterNetwork && !gateway.Address.Equals(IPAddress.Any)))
                checks.Add("WARNING: this adapter has no IPv4 LAN gateway and may be a VPN/virtual interface. Router forwarding normally targets the physical Wi-Fi/Ethernet address. Disconnect a VPN for the first test; it can also change the detected public IP and reply routing.");
        }
        var loopback = IPAddress.IsLoopback(IPAddress.Parse(settings.ListenAddress));
        if (loopback) checks.Add("Listener is desktop-only. A router forward cannot reach a loopback listener.");
        var listenerVerified = false;
        try
        {
            using var listener = new TcpClient();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await listener.ConnectAsync(IPAddress.Parse(settings.ListenAddress), settings.Port, timeout.Token);
            listenerVerified = true;
            checks.Add($"PASS: TCP listener accepted a local connection on {settings.ListenAddress}:{settings.Port}.");
        }
        catch (Exception exception) when (exception is SocketException or OperationCanceledException)
        { checks.Add($"FAIL: TCP listener could not be reached locally on {settings.ListenAddress}:{settings.Port}: {exception.Message}"); }
        if (loopback) checks.Add("Windows firewall: not applicable to a loopback-only listener.");
        else if (firewallRuleChecker is null) checks.Add($"Windows firewall: not inspected by this diagnostics host. Expected rule: '{DesktopRuleName(settings.Port)}'.");
        else checks.Add(await Task.Run(() => firewallRuleChecker(settings.Port))
            ? $"PASS: Windows Firewall has an enabled inbound TCP allow rule for local port {settings.Port}."
            : $"FAIL: Windows Firewall has no matching enabled inbound TCP allow rule for local port {settings.Port}. Expected rule: '{DesktopRuleName(settings.Port)}'.");
        if (!direct)
        {
            checks.Add("Direct internet is not configured. Use Set up direct internet while stopped.");
            checks.Add("Phone path: local listener and firewall prerequisites are reported above, but only the phone can verify the LAN route and pairing request.");
            return new(listenerVerified, address, checks.ToArray());
        }
        var origin = new Uri(settings.DirectOrigin);
        checks.Add($"Router: forward external TCP {origin.Port} to {settings.ListenAddress} TCP {settings.Port}. Reserve the desktop LAN address.");
        checks.Add($"Windows firewall: allow inbound TCP {settings.Port} for this desktop on the appropriate network profile. This diagnostic does not change or certify firewall/router rules.");
        checks.Add("WAN: use the router's public IPv4 address. Double NAT needs configuration at both routers; carrier-grade NAT requires a public address from the ISP.");
        var verified = false;
        try
        {
            var authorityPath = Path.Combine(Path.GetDirectoryName(settings.CertificatePath)!, "pocketagent-root.cer");
            using var authority = settings.PinnedCertificate ? X509CertificateLoader.LoadPkcs12FromFile(settings.CertificatePath, null, X509KeyStorageFlags.EphemeralKeySet) : File.Exists(authorityPath) ? X509CertificateLoader.LoadCertificateFromFile(authorityPath) : null;
            using var certificate = X509CertificateLoader.LoadPkcs12FromFile(settings.CertificatePath, null, X509KeyStorageFlags.EphemeralKeySet);
            checks.Add($"Certificate expires {certificate.NotAfter.ToUniversalTime():u}. Public-key fingerprint: {PeerCertificates.Fingerprint(certificate)}. Pair by scanning the desktop QR or pasting its full pairing string.");
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
        { checks.Add("FAIL: local HTTPS verification or connection failed. Check the server is running, its identity/certificate files exist, the certificate matches the public IP, and the listener is correct."); }
        checks.Add("QR/pairing-string trust requires no certificate installation, public CA, ports 80/443 or Caddy changes. Keep the desktop identity key and server.pfx private.");
        checks.Add("External access is NOT verified by a local test. Turn phone Wi-Fi off and connect using a fresh QR/pairing string to verify the actual public route.");
        checks.Add("No domain, central PocketAgent host or traffic relay is required. If the public IP changes, rerun direct setup and pair the phone at the new address; existing tokens are never forwarded to a different origin.");
        return new(verified, address, checks.ToArray());
    }

    private static string DesktopRuleName(int port) => $"PocketAgent inbound TCP {port}";
}
