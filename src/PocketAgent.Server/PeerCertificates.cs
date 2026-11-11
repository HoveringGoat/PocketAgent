using System.Net;
using System.Net.Security;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.Principal;

namespace PocketAgent;

// The QR establishes trust in this public key; no network-discovered pin is accepted.
public sealed class PeerCertificates(HostSettings settings) : BackgroundService
{
    private readonly List<X509Certificate2> retained = [];
    private X509Certificate2? current;
    private SslStreamCertificateContext? context;
    public string Status { get; private set; } = "HTTPS uses the configured certificate.";
    public X509Certificate2 Current
    {
        get { lock (retained) { if (current is null) { if (settings.PinnedCertificate) Provision(settings); Reload(); } return current!; } }
    }
    internal void ConfigureHttps(Microsoft.AspNetCore.Server.Kestrel.Https.HttpsConnectionAdapterOptions options)
    {
        options.ServerCertificate = Current;
        options.OnAuthenticate = (_, ssl) => { lock (retained) { ssl.ServerCertificate = null; ssl.ServerCertificateContext = context; } };
    }
    internal void Reload()
    {
        lock (retained)
        {
            var certificate = X509CertificateLoader.LoadPkcs12FromFile(settings.CertificatePath, null,
                OperatingSystem.IsWindows() ? X509KeyStorageFlags.UserKeySet : X509KeyStorageFlags.EphemeralKeySet);
            var chain = X509CertificateLoader.LoadPkcs12CollectionFromFile(settings.CertificatePath, null, X509KeyStorageFlags.EphemeralKeySet);
            retained.AddRange(chain.Cast<X509Certificate2>()); retained.Add(certificate);
            context = SslStreamCertificateContext.Create(certificate, chain, offline: true);
            current = certificate;
        }
    }
    public static string Fingerprint(X509Certificate2 certificate) => Convert.ToHexString(SHA256.HashData(certificate.PublicKey.ExportSubjectPublicKeyInfo()));
    public static string PairingPayload(HostSettings settings, string ticket)
    {
        if (!settings.UseHttps) throw new InvalidOperationException("Set up HTTPS before displaying a pairing QR.");
        using var certificate = X509CertificateLoader.LoadPkcs12FromFile(settings.CertificatePath, null, X509KeyStorageFlags.EphemeralKeySet);
        return $"pocketagent://pair?v=1&origin={Uri.EscapeDataString(settings.ClientOrigin)}&spki={Fingerprint(certificate)}&ticket={ticket}";
    }

    public static void Provision(HostSettings settings, bool force = false)
    {
        settings.Validate();
        if (!settings.PinnedCertificate || Path.GetFullPath(settings.CertificatePath) != Path.Combine(settings.DataDirectory, "certificates", "device", "server.pfx"))
            throw new InvalidOperationException("QR certificates must use the managed device certificate path.");
        var directory = Path.GetDirectoryName(settings.CertificatePath)!;
        Directory.CreateDirectory(directory);
        if (OperatingSystem.IsWindows())
        {
            var acl = new DirectorySecurity(); acl.SetAccessRuleProtection(true, false);
            acl.AddAccessRule(new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!, FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            new DirectoryInfo(directory).SetAccessControl(acl);
        }
        else File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        using var ownership = new FileStream(Path.Combine(directory, "certificate.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var keyPath = Path.Combine(directory, "identity.pem");
        using var key = RSA.Create(3072);
        if (File.Exists(keyPath)) key.ImportFromPem(File.ReadAllText(keyPath));
        else
        {
            if (File.Exists(settings.CertificatePath)) throw new InvalidOperationException("The saved identity key is missing. Restore it instead of silently replacing the trusted identity.");
            WriteAtomic(keyPath, System.Text.Encoding.UTF8.GetBytes(key.ExportPkcs8PrivateKeyPem()));
        }
        if (!force && File.Exists(settings.CertificatePath))
        {
            using var old = X509CertificateLoader.LoadPkcs12FromFile(settings.CertificatePath, null, X509KeyStorageFlags.EphemeralKeySet);
            if (!CryptographicOperations.FixedTimeEquals(old.PublicKey.ExportSubjectPublicKeyInfo(), key.ExportSubjectPublicKeyInfo()))
                throw new InvalidOperationException("The certificate does not match the saved desktop identity.");
            if (old.NotAfter.ToUniversalTime() > DateTime.UtcNow.AddDays(30) && old.MatchesHostname(new Uri(settings.ClientOrigin).Host, false, false)) return;
        }
        var request = new CertificateRequest("CN=PocketAgent", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var names = new SubjectAlternativeNameBuilder(); names.AddIpAddress(IPAddress.Parse(settings.ListenAddress));
        var host = new Uri(settings.ClientOrigin).Host;
        if (IPAddress.TryParse(host, out var address)) names.AddIpAddress(address); else names.AddDnsName(host);
        request.CertificateExtensions.Add(names.Build());
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new("1.3.6.1.5.5.7.3.1") }, true));
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddYears(1));
        WriteAtomic(settings.CertificatePath, certificate.Export(X509ContentType.Pfx));
    }
    private static void WriteAtomic(string path, byte[] data)
    {
        var temporary = path + ".tmp";
        try { File.WriteAllBytes(temporary, data); File.Move(temporary, path, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!settings.PinnedCertificate) return;
        while (!stoppingToken.IsCancellationRequested)
        {
            try { Provision(settings); Reload(); Status = $"QR identity: certificate expires {Current.NotAfter.ToLocalTime():d}. Renewed locally with the same key."; }
            catch (Exception) { Status = "Local certificate renewal failed. Check desktop identity files and permissions; expired certificates block access."; }
            await Task.Delay(TimeSpan.FromHours(24), stoppingToken);
        }
    }
    public override void Dispose() { base.Dispose(); lock (retained) { foreach (var certificate in retained) certificate.Dispose(); retained.Clear(); } }
}
