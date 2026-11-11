using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using PocketAgent;

var setup = args.Contains("--setup", StringComparer.Ordinal);
var revoke = args.Contains("--revoke-devices", StringComparer.Ordinal);
var hostArgs = args.Where(argument => argument is not "--setup" and not "--revoke-devices").ToArray();
if (setup || revoke)
{
    var configuration = new ConfigurationBuilder().AddEnvironmentVariables().AddCommandLine(hostArgs).Build();
    var settings = configuration.GetSection("PocketAgent").Get<HostSettings>() ?? new();
    settings.Validate(requireRepository: false, creatingCertificate: setup);
    if (setup) CertificateSetup.Create(settings.DataDirectory, settings.ListenAddress);
    else { new LocalStore(settings).RevokeAll(); Console.WriteLine("All device sessions revoked."); }
    return;
}
await using var app = RelayHost.Create(hostArgs);
app.Services.GetRequiredService<PairingCode>().Display();
await app.RunAsync();
