using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using PocketAgent;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;

internal static class AcmeChecks
{
    public static async Task Run()
    {
        var socket = new TcpListener(IPAddress.Loopback, 0);
        socket.Start(); var port = ((IPEndPoint)socket.LocalEndpoint).Port; socket.Stop();
        const string token = "test_challenge_token_1234567890";
        await using (var listener = AcmeChallengeListener.Create(IPAddress.Loopback, port, "203.0.113.10", token, token + ".thumbprint"))
        {
            await listener.StartAsync();
            using var http = new HttpClient(new SocketsHttpHandler { UseProxy = false });
            foreach (var path in new[] { "/", "/api/session", "/api/devices/pair", "/.well-known/acme-challenge/wrong", "/.well-known/acme-challenge/" + token + "?x=1" })
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{port}{path}"); request.Headers.Host = "203.0.113.10";
                using var response = await http.SendAsync(request);
                Check(response.StatusCode == HttpStatusCode.NotFound, "Challenge host leaked another route.");
            }
            using var correct = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{port}/.well-known/acme-challenge/{token}"); correct.Headers.Host = "203.0.113.10";
            using var accepted = await http.SendAsync(correct);
            Check(await accepted.Content.ReadAsStringAsync() == token + ".thumbprint", "Correct challenge failed.");
            using var wrongHost = await http.GetAsync($"http://127.0.0.1:{port}/.well-known/acme-challenge/{token}");
            Check(wrongHost.StatusCode == HttpStatusCode.NotFound, "Wrong host accepted.");
            await listener.StopAsync();
        }
        Console.WriteLine("PASS isolated challenge host rejects API paths, wrong token, query and wrong Host; correct token succeeds");

        var directory = Path.Combine(Directory.GetCurrentDirectory(), ".local", "acme-check-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var settings = new HostSettings { RepositoryPath = Directory.GetCurrentDirectory(), DataDirectory = directory, ListenAddress = "127.0.0.1", DirectOrigin = "https://203.0.113.10:5147", CertificatePath = Path.Combine(directory, "server.pfx"), AcmePort = port, AutomaticCertificate = true };
        await File.WriteAllTextAsync(settings.CertificatePath, "preserved fixture");
        var fixture = new AcmeFixture(port, token);
        await AutomaticCertificates.IssueCore(settings, true, fixture);
        Check(fixture.Validated && fixture.Finalized, "ACME fixture did not complete validation and CSR signing.");
        Check(await File.ReadAllTextAsync(settings.CertificatePath) == "preserved fixture", "Staging replaced production certificate.");
        var failed = new AcmeFixture(port, token) { FailValidation = true };
        try { await AutomaticCertificates.IssueCore(settings, true, failed); throw new Exception("Validation failure accepted."); }
        catch (InvalidOperationException) { }
        Check(await File.ReadAllTextAsync(settings.CertificatePath) == "preserved fixture", "Failure replaced existing certificate.");
        using (var probe = new TcpListener(IPAddress.Loopback, port)) { probe.Start(); probe.Stop(); }
        using var key = RSA.Create(2048);
        var requestCertificate = new CertificateRequest("CN=test", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var certificate = requestCertificate.CreateSelfSigned(DateTimeOffset.UtcNow.AddHours(-80), DateTimeOffset.UtcNow.AddHours(80));
        Check(AutomaticCertificates.RenewalDue(certificate, DateTimeOffset.UtcNow.AddMinutes(1)), "Half-life renewal not due.");
        Check(!AutomaticCertificates.RenewalDue(certificate, DateTimeOffset.UtcNow.AddHours(-10)), "Early renewal requested.");
        try { AutomaticCertificates.ValidateIssued(certificate.Export(X509ContentType.Pfx), "203.0.113.10"); throw new Exception("Untrusted/mismatched certificate accepted."); }
        catch (InvalidOperationException) { }
        Console.WriteLine("PASS ACME IP/shortlived order, HTTP validation, IP SAN CSR, staging isolation, failure preservation, listener cleanup, renewal timing and public-trust rejection (no external requests)");
        await CheckCertificateReload(settings, port);
    }

    private static async Task CheckCertificateReload(HostSettings settings, int port)
    {
        using var caKey = RSA.Create(2048);
        var caRequest = new CertificateRequest("CN=Reload Fixture CA", caKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        caRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        using var ca = caRequest.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(8));
        string SaveLeaf()
        {
            using var key = RSA.Create(2048);
            var csr = new CertificateRequest("CN=Reload", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            var names = new SubjectAlternativeNameBuilder(); names.AddIpAddress(IPAddress.Parse("203.0.113.10")); csr.CertificateExtensions.Add(names.Build());
            using var publicLeaf = csr.Create(ca, DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(160), RandomNumberGenerator.GetBytes(16));
            using var leaf = publicLeaf.CopyWithPrivateKey(key);
            File.WriteAllBytes(settings.CertificatePath, new X509Certificate2Collection { leaf, ca }.Export(X509ContentType.Pfx)!);
            return leaf.Thumbprint;
        }
        var first = SaveLeaf();
        using var certificates = new AutomaticCertificates(settings);
        var builder = WebApplication.CreateSlimBuilder(); builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, port, listen => listen.UseHttps(certificates.ConfigureHttps)));
        await using var app = builder.Build(); app.MapGet("/", () => "fixture"); await app.StartAsync();
        async Task CheckHandshake(string expected)
        {
            var policy = new X509ChainPolicy { TrustMode = X509ChainTrustMode.CustomRootTrust, RevocationMode = X509RevocationMode.NoCheck, DisableCertificateDownloads = true }; policy.CustomTrustStore.Add(ca);
            using var handler = new SocketsHttpHandler
            {
                UseProxy = false,
                SslOptions = new() { CertificateChainPolicy = policy, RemoteCertificateValidationCallback = (_, remote, _, errors) => errors == System.Net.Security.SslPolicyErrors.None && remote!.GetCertHashString() == expected },
                ConnectCallback = async (_, cancellation) => { var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp); await socket.ConnectAsync(IPAddress.Loopback, port, cancellation); return new NetworkStream(socket, true); }
            };
            using var client = new HttpClient(handler); Check(await client.GetStringAsync($"https://203.0.113.10:{port}/") == "fixture", "TLS reload handshake failed.");
        }
        await CheckHandshake(first);
        var second = SaveLeaf(); certificates.Reload(); await CheckHandshake(second);
        await app.StopAsync();
        Console.WriteLine("PASS real Kestrel TLS verifies IP/chain and serves rotated certificate on new connections without restarting listener");
    }

    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }

    private sealed class AcmeFixture(int port, string token) : HttpMessageHandler
    {
        public bool Validated, Finalized, FailValidation;
        private string? certificatePem;
        private const string Origin = "https://acme-staging-v02.api.letsencrypt.org";
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            JsonElement payload = default;
            if (request.Content is not null)
            {
                using var envelope = JsonDocument.Parse(await request.Content.ReadAsStringAsync(cancellationToken));
                var encoded = envelope.RootElement.GetProperty("payload").GetString()!;
                if (encoded.Length > 0) payload = JsonSerializer.Deserialize<JsonElement>(Decode(encoded));
            }
            object body = new { };
            string? location = null;
            if (path == "/directory") body = new { newNonce = Origin + "/nonce", newAccount = Origin + "/account", newOrder = Origin + "/new-order", meta = new { profiles = new { shortlived = "test" } } };
            else if (path == "/account") { location = Origin + "/account/1"; body = new { status = "valid" }; }
            else if (path == "/new-order")
            {
                Check(payload.GetProperty("profile").GetString() == "shortlived", "Wrong profile.");
                var identifier = payload.GetProperty("identifiers")[0];
                Check(identifier.GetProperty("type").GetString() == "ip" && identifier.GetProperty("value").GetString() == "203.0.113.10", "Wrong ACME identifier.");
                location = Origin + "/order"; body = Order();
            }
            else if (path == "/order") body = Order();
            else if (path == "/authorization") body = new { status = Validated ? FailValidation ? "invalid" : "valid" : "pending", identifier = new { type = "ip", value = "203.0.113.10" }, challenges = new[] { new { type = "http-01", url = Origin + "/challenge", token, status = "pending" } } };
            else if (path == "/challenge")
            {
                if (payload.ValueKind == JsonValueKind.Undefined)
                    return Reply(new StringContent(JsonSerializer.Serialize(new { type = "http-01", url = Origin + "/challenge", token, status = "pending" }), Encoding.UTF8, "application/json"), null);
                using var client = new HttpClient(new SocketsHttpHandler { UseProxy = false });
                using var validation = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{port}/.well-known/acme-challenge/{token}"); validation.Headers.Host = "203.0.113.10";
                using var answer = await client.SendAsync(validation, cancellationToken);
                Check(answer.IsSuccessStatusCode && (await answer.Content.ReadAsStringAsync(cancellationToken)).StartsWith(token + ".", StringComparison.Ordinal), "Validation listener did not prove account key possession.");
                Validated = true; body = new { type = "http-01", url = Origin + "/challenge", token, status = "valid" };
            }
            else if (path == "/finalize")
            {
                var csr = CertificateRequest.LoadSigningRequest(Decode(payload.GetProperty("csr").GetString()!), HashAlgorithmName.SHA256, CertificateRequestLoadOptions.UnsafeLoadCertificateExtensions, RSASignaturePadding.Pkcs1);
                var names = csr.CertificateExtensions.OfType<X509SubjectAlternativeNameExtension>().Single();
                Check(names.EnumerateIPAddresses().Single().ToString() == "203.0.113.10" && !csr.SubjectName.Name!.Contains("CN=", StringComparison.Ordinal), "CSR must contain IP SAN and no CommonName.");
                using var caKey = RSA.Create(2048);
                var caRequest = new CertificateRequest("CN=Fixture CA", caKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
                caRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
                using var ca = caRequest.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(8));
                using var leaf = csr.Create(ca, DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(160), RandomNumberGenerator.GetBytes(16));
                certificatePem = leaf.ExportCertificatePem() + "\n" + ca.ExportCertificatePem(); Finalized = true; body = Order();
            }
            else if (path == "/certificate")
                return Reply(new StringContent(certificatePem!, Encoding.UTF8, "application/pem-certificate-chain"), null);
            else if (path != "/nonce") throw new InvalidOperationException("Unexpected fixture endpoint " + path);
            return Reply(new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"), location);
        }
        private object Order() => new { status = Finalized ? "valid" : Validated ? "ready" : "pending", identifiers = new[] { new { type = "ip", value = "203.0.113.10" } }, authorizations = new[] { Origin + "/authorization" }, finalize = Origin + "/finalize", certificate = Finalized ? Origin + "/certificate" : null };
        private static HttpResponseMessage Reply(HttpContent content, string? location)
        {
            var result = new HttpResponseMessage(location is null ? HttpStatusCode.OK : HttpStatusCode.Created) { Content = content };
            result.Headers.Add("Replay-Nonce", Guid.NewGuid().ToString("N"));
            if (location is not null) result.Headers.Location = new Uri(location);
            return result;
        }
        private static byte[] Decode(string encoded) => Convert.FromBase64String(encoded.Replace('-', '+').Replace('_', '/') + new string('=', (4 - encoded.Length % 4) % 4));
    }
}
