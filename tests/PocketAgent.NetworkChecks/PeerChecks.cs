using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using PocketAgent;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;

internal static class PeerChecks
{
    public static async Task Run()
    {
        var directory = Path.Combine(Directory.GetCurrentDirectory(), ".local", "qr-fixture"); Directory.CreateDirectory(directory);
        var settings = new HostSettings { RepositoryPath = Directory.GetCurrentDirectory(), DataDirectory = directory, ListenAddress = "127.0.0.1", DirectOrigin = "https://203.0.113.10:5147", CertificatePath = Path.Combine(directory, "certificates", "device", "server.pfx"), PinnedCertificate = true };
        PeerCertificates.Provision(settings);
        using var first = X509CertificateLoader.LoadPkcs12FromFile(settings.CertificatePath, null, X509KeyStorageFlags.EphemeralKeySet);
        var pin = PeerCertificates.Fingerprint(first);
        PeerCertificates.Provision(settings, force: true);
        using var second = X509CertificateLoader.LoadPkcs12FromFile(settings.CertificatePath, null, X509KeyStorageFlags.EphemeralKeySet);
        Check(pin == PeerCertificates.Fingerprint(second) && first.Thumbprint != second.Thumbprint, "Local renewal changed desktop identity or did not rotate certificate.");
        var pairing = new PairingCode(); var ticket = pairing.Ticket;
        var payload = PeerCertificates.PairingPayload(settings, ticket);
        Check(payload.Contains("spki=" + pin, StringComparison.Ordinal) && payload.Contains("ticket=" + ticket, StringComparison.Ordinal) && !payload.Contains("PRIVATE", StringComparison.Ordinal), "QR payload mismatch.");
        Check(!pairing.Consume(new string('0',64)) && pairing.Consume(ticket) && !pairing.Consume(ticket) && !pairing.Consume(pairing.Current.Code), "Ticket must authorize once and share consumption with the legacy code.");
        pairing.Renew(); Check(pairing.Ticket != ticket && !pairing.Consume(ticket), "Renewed QR must invalidate the prior ticket.");
        typeof(PairingCode).GetField("expires", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.SetValue(pairing, DateTimeOffset.UtcNow.AddSeconds(-1));
        Check(!pairing.Consume(pairing.Ticket), "Expired ticket accepted.");
        using (var key = RSA.Create(2048))
        {
            byte[] Certificate(bool expired, string ip)
            {
                var csr = new CertificateRequest("CN=TLS fixture", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
                var names = new SubjectAlternativeNameBuilder(); names.AddIpAddress(IPAddress.Parse(ip)); csr.CertificateExtensions.Add(names.Build());
                csr.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
                csr.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new("1.3.6.1.5.5.7.3.1") }, true));
                using var cert = csr.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-3), expired ? DateTimeOffset.UtcNow.AddDays(-1) : DateTimeOffset.UtcNow.AddDays(5));
                return cert.Export(X509ContentType.Pfx);
            }
            File.WriteAllText(Path.Combine(directory, "android-fixture.json"), JsonSerializer.Serialize(new
            {
                valid = Convert.ToBase64String(Certificate(false, "127.0.0.1")), expired = Convert.ToBase64String(Certificate(true, "127.0.0.1")),
                pin = Convert.ToHexString(SHA256.HashData(key.ExportSubjectPublicKeyInfo()))
            }));
        }
        Console.WriteLine("PASS QR identity, same-key local renewal, ticket single use/rotation/expiry; generated isolated Android TLS fixtures");

        var socket = new TcpListener(IPAddress.Loopback, 0); socket.Start(); var port = ((IPEndPoint)socket.LocalEndpoint).Port; socket.Stop(); settings.Port = port; settings.DirectOrigin = $"https://203.0.113.10:{port}";
        settings.CodexExecutable = Environment.GetEnvironmentVariable("POCKETAGENT_TEST_NODE") ?? throw new InvalidOperationException("Set POCKETAGENT_TEST_NODE to the native Node executable for the deterministic provider fixture.");
        settings.CodexArguments = [Path.Combine(Directory.GetCurrentDirectory(), "tests", "fixtures", "codex.mjs")];
        await using var app = RelayHost.Create([], settings); await app.StartAsync();
        async Task CheckTls(X509Certificate2 trusted)
        {
            var policy = new X509ChainPolicy { TrustMode = X509ChainTrustMode.CustomRootTrust, RevocationMode = X509RevocationMode.NoCheck, DisableCertificateDownloads = true }; policy.CustomTrustStore.Add(trusted);
            using var handler = new SocketsHttpHandler { UseProxy = false, SslOptions = new() { CertificateChainPolicy = policy }, ConnectCallback = async (_, cancellation) => { var sock = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp); await sock.ConnectAsync(IPAddress.Loopback, port, cancellation); return new NetworkStream(sock, true); } };
            using var client = new HttpClient(handler);
            Check((await client.GetStringAsync(settings.DirectOrigin + "/api/session")).Contains("version", StringComparison.Ordinal), "Pinned identity TLS failed.");
            var codes = app.Services.GetRequiredService<PairingCode>(); codes.Renew();
            client.DefaultRequestHeaders.Add("X-PocketAgent", "1");
            var body = JsonSerializer.Serialize(new { code = codes.Ticket, deviceName = "Isolated QR check" });
            using var enrolled = await client.PostAsync(settings.DirectOrigin + "/api/devices/pair", new StringContent(body, System.Text.Encoding.UTF8, "application/json"));
            Check(enrolled.StatusCode == HttpStatusCode.OK, "QR ticket API enrollment failed.");
            using var paired = JsonDocument.Parse(await enrolled.Content.ReadAsStringAsync());
            using var reused = await client.PostAsync(settings.DirectOrigin + "/api/devices/pair", new StringContent(body, System.Text.Encoding.UTF8, "application/json"));
            Check(reused.StatusCode == HttpStatusCode.Unauthorized, "Replayed QR ticket accepted.");
            client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", paired.RootElement.GetProperty("token").GetString());
            using var protectedResponse = await client.GetAsync(settings.DirectOrigin + "/api/conversations");
            Check(protectedResponse.IsSuccessStatusCode, "Paired token did not authorize conversation access.");
        }
        await CheckTls(second); PeerCertificates.Provision(settings, true); app.Services.GetRequiredService<PeerCertificates>().Reload();
        using var rotated = X509CertificateLoader.LoadPkcs12FromFile(settings.CertificatePath, null, X509KeyStorageFlags.EphemeralKeySet); await CheckTls(rotated);
        await app.StopAsync();
        Console.WriteLine("PASS real HTTPS IP verification and local certificate hot reload without listener restart");
    }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
