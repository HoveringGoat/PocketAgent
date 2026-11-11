using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace PocketAgent;

public sealed class HostSettings
{
    public string ListenAddress { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 5147;
    public string RepositoryPath { get; set; } = "";
    public string DataDirectory { get; set; } = "";
    public string CertificatePath { get; set; } = "";
    public string CodexExecutable { get; set; } = "";
    public string CodexExtensionsDirectory { get; set; } = "";
    public string PublicOrigin { get; set; } = "";
    public string DirectOrigin { get; set; } = "";
    public bool PinnedCertificate { get; set; }
    public string PermissionMode { get; set; } = "inherit";
    public string[] CodexArguments { get; set; } = [];
    public bool UseHttps => CertificatePath.Length > 0;
    public string Origin => $"{(UseHttps ? "https" : "http")}://{ListenAddress}:{Port}";
    public string ClientOrigin => DirectOrigin.Length > 0 ? DirectOrigin : PublicOrigin.Length > 0 ? PublicOrigin : Origin;

    public void Validate(bool requireRepository = true, bool creatingCertificate = false)
    {
        if (PermissionMode is not ("inherit" or "ask" or "full-access"))
            throw new InvalidOperationException("PermissionMode must be inherit, ask, or full-access.");
        if (DataDirectory.Length == 0)
            DataDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PocketAgent");
        DataDirectory = Path.GetFullPath(DataDirectory);
        Directory.CreateDirectory(DataDirectory);
        if (!IPAddress.TryParse(ListenAddress, out var address) || address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
            throw new InvalidOperationException("ListenAddress must be an explicit IPv4 address.");
        if (!IPAddress.IsLoopback(address) && (!IsPrivate(address) || (!UseHttps && !creatingCertificate)))
            throw new InvalidOperationException("LAN access requires a private IPv4 address and CertificatePath. Run certificate setup first.");
        if (Port is < 1024 or > 65535)
            throw new InvalidOperationException("Port must be between 1024 and 65535.");
        if (PinnedCertificate && !UseHttps) throw new InvalidOperationException("QR trust requires HTTPS.");
        if (DirectOrigin.Length > 0)
        {
            if (!Uri.TryCreate(DirectOrigin, UriKind.Absolute, out var direct) || direct.Scheme != "https" ||
                direct.UserInfo.Length > 0 || direct.AbsolutePath != "/" || direct.Query.Length > 0 || direct.Fragment.Length > 0 || direct.Port is < 1 or > 65535 ||
                !IPAddress.TryParse(direct.Host, out var publicIp) || !IsPublicIPv4(publicIp))
                throw new InvalidOperationException("Direct internet address must be https://<public-IPv4>:<external-port> without a path or credentials. Use your router's WAN address, not a private or carrier-grade NAT address.");
            if (PublicOrigin.Length > 0 || !UseHttps && !creatingCertificate)
                throw new InvalidOperationException("Direct internet requires a server certificate and cannot be combined with proxy/tunnel mode.");
            DirectOrigin = direct.GetLeftPart(UriPartial.Authority);
        }
        if (PublicOrigin.Length > 0)
        {
            if (!Uri.TryCreate(PublicOrigin, UriKind.Absolute, out var origin) || origin.Scheme != "https" ||
                origin.UserInfo.Length > 0 || origin.AbsolutePath != "/" || origin.Query.Length > 0 || origin.Fragment.Length > 0)
                throw new InvalidOperationException("PublicOrigin must be an HTTPS origin without credentials, path, query, or fragment.");
            if (!IPAddress.IsLoopback(address))
                throw new InvalidOperationException("Public-origin proxy mode requires a loopback listener.");
            PublicOrigin = origin.GetLeftPart(UriPartial.Authority);
        }
        if (requireRepository && (string.IsNullOrWhiteSpace(RepositoryPath) || !Directory.Exists(RepositoryPath)))
            throw new InvalidOperationException("Set PocketAgent__RepositoryPath to an existing repository directory on the desktop.");
        if (requireRepository)
            RepositoryPath = Path.GetFullPath(RepositoryPath);
        if (CodexArguments.Length == 0)
            CodexArguments = ["app-server", "--listen", "stdio://"];
    }

    public static bool IsPublicIPv4(IPAddress address)
    {
        if (address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) return false;
        var bytes = address.GetAddressBytes();
        return !IPAddress.IsLoopback(address) && !IsPrivate(address) && bytes[0] is > 0 and < 224 &&
            !(bytes[0] == 100 && bytes[1] is >= 64 and <= 127) && !(bytes[0] == 169 && bytes[1] == 254);
    }

    private static bool IsPrivate(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        return bytes[0] == 10 || (bytes[0] == 172 && bytes[1] is >= 16 and <= 31) || (bytes[0] == 192 && bytes[1] == 168);
    }
}

public static class CertificateSetup
{
    public static void Create(string directory, string ip, string? additionalIp = null)
    {
        if (!IPAddress.TryParse(ip, out var address) || address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
            throw new InvalidOperationException("Certificate setup requires an IPv4 address.");
        Directory.CreateDirectory(directory);
        var pfxPath = Path.Combine(directory, "server.pfx");
        if (File.Exists(pfxPath))
            throw new InvalidOperationException("A server certificate already exists. Choose a new output directory to rotate certificates.");
        using var caKey = RSA.Create(3072);
        var caRequest = new CertificateRequest("CN=PocketAgent Local Root", caKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        caRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        caRequest.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        using var ca = caRequest.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddYears(2));
        using var serverKey = RSA.Create(3072);
        var request = new CertificateRequest($"CN={ip}", serverKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var names = new SubjectAlternativeNameBuilder();
        names.AddIpAddress(address);
        if (additionalIp is not null)
        {
            if (!IPAddress.TryParse(additionalIp, out var additional) || additional.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
                throw new InvalidOperationException("Additional certificate address must be IPv4.");
            if (!additional.Equals(address)) names.AddIpAddress(additional);
        }
        request.CertificateExtensions.Add(names.Build());
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new("1.3.6.1.5.5.7.3.1") }, true));
        using var certificate = request.Create(ca, DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(365), RandomNumberGenerator.GetBytes(16));
        using var server = certificate.CopyWithPrivateKey(serverKey);
        File.WriteAllBytes(pfxPath, server.Export(X509ContentType.Pfx));
        File.WriteAllBytes(Path.Combine(directory, "pocketagent-root.cer"), ca.Export(X509ContentType.Cert));
        Console.WriteLine($"Server certificate: {pfxPath}");
        Console.WriteLine($"Install ONLY pocketagent-root.cer on Android. Keep server.pfx private. Certificate address: {ip}");
    }
}
