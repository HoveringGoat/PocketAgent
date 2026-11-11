using System.Net;
using System.Net.Security;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.Principal;
using Certes;
using Certes.Acme;
using Certes.Acme.Resource;

namespace PocketAgent;

public sealed class AutomaticCertificates(HostSettings settings) : BackgroundService
{
    private readonly List<X509Certificate2> retained = [];
    private X509Certificate2? current;
    private SslStreamCertificateContext? context;
    private DateTimeOffset retryAt;
    public string Status { get; private set; } = "Automatic certificate renewal is disabled.";

    public X509Certificate2 Current
    {
        get
        {
            lock (retained)
            {
                if (current is null) Install(Load(settings.CertificatePath));
                return current!;
            }
        }
    }
    public SslStreamCertificateContext Context { get { _ = Current; lock (retained) return context!; } }

    internal void ConfigureHttps(Microsoft.AspNetCore.Server.Kestrel.Https.HttpsConnectionAdapterOptions options)
    {
        options.ServerCertificate = Current;
        options.OnAuthenticate = (_, ssl) => { ssl.ServerCertificate = null; ssl.ServerCertificateContext = Context; };
    }

    internal void Reload() => Install(Load(settings.CertificatePath));

    private static X509Certificate2 Load(string path) => X509CertificateLoader.LoadPkcs12FromFile(path, null,
        OperatingSystem.IsWindows() ? X509KeyStorageFlags.UserKeySet : X509KeyStorageFlags.EphemeralKeySet);

    private void Install(X509Certificate2 certificate)
    {
        // Keep old instances alive for handshakes already using them, until host shutdown.
        lock (retained)
        {
            var chain = X509CertificateLoader.LoadPkcs12CollectionFromFile(settings.CertificatePath, null, X509KeyStorageFlags.EphemeralKeySet);
            retained.AddRange(chain.Cast<X509Certificate2>());
            context = SslStreamCertificateContext.Create(certificate, chain, offline: true);
            retained.Add(certificate); current = certificate;
        }
    }

    public static bool RenewalDue(X509Certificate2 certificate, DateTimeOffset now) =>
        now >= certificate.NotBefore.ToUniversalTime() + (certificate.NotAfter.ToUniversalTime() - certificate.NotBefore.ToUniversalTime()) / 2;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!settings.AutomaticCertificate) return;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (DateTimeOffset.UtcNow >= retryAt && RenewalDue(Current, DateTimeOffset.UtcNow))
                {
                    Status = "Renewing HTTPS certificate…";
                    await Issue(settings, false, stoppingToken);
                    Reload();
                }
                if (DateTimeOffset.UtcNow >= retryAt)
                    Status = $"Automatic HTTPS: expires {Current.NotAfter.ToLocalTime():g}. Renewal runs while the server is running.";
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception)
            {
                retryAt = DateTimeOffset.UtcNow.AddHours(6);
                Status = "HTTPS renewal failed; retry in six hours. Check TCP 80 forwarding/firewall and internet access. An expired certificate blocks phone access.";
            }
            await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken);
        }
    }

    public static async Task Issue(HostSettings settings, bool staging, CancellationToken cancellationToken = default)
        => await IssueCore(settings, staging, null, cancellationToken);

    internal static async Task IssueCore(HostSettings settings, bool staging, HttpMessageHandler? transport, CancellationToken cancellationToken = default)
    {
        settings.Validate();
        if (settings.DirectOrigin.Length == 0) throw new InvalidOperationException("Configure direct internet first.");
        var ip = new Uri(settings.DirectOrigin).Host;
        var directory = Path.Combine(settings.DataDirectory, "acme", staging ? "staging" : "production", ip);
        System.IO.Directory.CreateDirectory(directory);
        ProtectDirectory(directory);
        await using var ownership = new FileStream(Path.Combine(directory, "issuance.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromMinutes(4));
        using var handler = new CancellationHandler(deadline.Token, transport);
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
        var endpoint = staging ? WellKnownServers.LetsEncryptStagingV2 : WellKnownServers.LetsEncryptV2;
        var accountPath = Path.Combine(directory, "account.pem");
        var accountKey = File.Exists(accountPath) ? KeyFactory.FromPem(await File.ReadAllTextAsync(accountPath, deadline.Token)) : KeyFactory.NewKey(KeyAlgorithm.ES256);
        if (!File.Exists(accountPath)) await AtomicWrite(accountPath, System.Text.Encoding.UTF8.GetBytes(accountKey.ToPem()), deadline.Token);
        var acme = new AcmeContext(endpoint, accountKey, new AcmeHttpClient(endpoint, http));
        await acme.NewAccount(Array.Empty<string>(), termsOfServiceAgreed: true);
        var order = await acme.NewOrderWithProfile(new[] { ip }, "shortlived");
        foreach (var authorization in await order.Authorizations())
        {
            deadline.Token.ThrowIfCancellationRequested();
            var resource = await authorization.Resource();
            if (resource.Identifier.Type != IdentifierType.Ip || resource.Identifier.Value != ip)
                throw new InvalidOperationException("Unexpected certificate authorization identity.");
            if (resource.Status == AuthorizationStatus.Valid) continue;
            var challenge = await authorization.Http();
            var challengeResource = await challenge.Resource();
            await using var listener = AcmeChallengeListener.Create(IPAddress.Parse(settings.ListenAddress), settings.AcmePort, ip, challengeResource.Token, challenge.KeyAuthz);
            await listener.StartAsync(deadline.Token);
            try
            {
                await challenge.Validate();
                while (true)
                {
                    await Task.Delay(TimeSpan.FromSeconds(2), deadline.Token);
                    var state = await authorization.Resource();
                    if (state.Status == AuthorizationStatus.Valid) break;
                    if (state.Status != AuthorizationStatus.Pending) throw new InvalidOperationException("Certificate validation failed. Check external TCP 80 forwarding.");
                }
            }
            finally { await listener.StopAsync(CancellationToken.None); }
        }
        var key = KeyFactory.NewKey(KeyAlgorithm.RS256);
        // IP subjects must be in SAN; leave CommonName empty.
        var chain = await order.Generate(new CsrInfo(), key);
        var pfx = chain.ToPfx(key).Build("PocketAgent", "");
        if (!staging)
        {
            ValidateIssued(pfx, ip);
            ProtectDirectory(Path.GetDirectoryName(settings.CertificatePath)!);
            await AtomicWrite(settings.CertificatePath, pfx, deadline.Token);
        }
    }

    public static void ValidateIssued(byte[] pfx, string ip)
    {
        var certificates = X509CertificateLoader.LoadPkcs12Collection(pfx, null, X509KeyStorageFlags.EphemeralKeySet);
        try
        {
            var leaf = certificates.Cast<X509Certificate2>().Single(certificate => certificate.HasPrivateKey);
            using var chain = new X509Chain();
            chain.ChainPolicy.ExtraStore.AddRange(certificates);
            chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
            chain.ChainPolicy.DisableCertificateDownloads = true;
            chain.ChainPolicy.ApplicationPolicy.Add(new Oid("1.3.6.1.5.5.7.3.1"));
            if (!leaf.MatchesHostname(ip, allowWildcards: false, allowCommonName: false) ||
                leaf.NotAfter.ToUniversalTime() <= DateTime.UtcNow.AddHours(24) || !chain.Build(leaf))
                throw new InvalidOperationException("Issued certificate did not pass public trust, IP identity and validity checks.");
        }
        finally { foreach (var certificate in certificates) certificate.Dispose(); }
    }

    private static async Task AtomicWrite(string path, byte[] bytes, CancellationToken token)
    {
        var temporary = path + ".tmp";
        try { await File.WriteAllBytesAsync(temporary, bytes, token); File.Move(temporary, path, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static void ProtectDirectory(string path)
    {
        System.IO.Directory.CreateDirectory(path);
        if (OperatingSystem.IsWindows())
        {
            var acl = new DirectorySecurity();
            acl.SetAccessRuleProtection(true, false);
            acl.AddAccessRule(new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!, FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            new DirectoryInfo(path).SetAccessControl(acl);
        }
        else File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    private sealed class CancellationHandler(CancellationToken lifetime, HttpMessageHandler? transport) : DelegatingHandler(transport ?? new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false, UseProxy = false })
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(lifetime, cancellationToken);
            return await base.SendAsync(request, linked.Token);
        }
    }

    public override void Dispose()
    {
        base.Dispose();
        lock (retained) { foreach (var certificate in retained) certificate.Dispose(); retained.Clear(); }
    }
}
