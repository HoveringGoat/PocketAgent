using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using PocketAgent;

var setup = args.Contains("--setup", StringComparer.Ordinal);
var revoke = args.Contains("--revoke-devices", StringComparer.Ordinal);
var diagnose = args.Contains("--diagnose", StringComparer.Ordinal);
var hostArgs = args.Where(argument => argument is not "--setup" and not "--revoke-devices" and not "--diagnose").ToArray();
if (setup || revoke || diagnose)
{
    var configuration = new ConfigurationBuilder().AddEnvironmentVariables().AddCommandLine(hostArgs).Build();
    var settings = configuration.GetSection("PocketAgent").Get<HostSettings>() ?? new();
    settings.Validate(requireRepository: false, creatingCertificate: setup);
    if (diagnose) Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(await new ConnectivityDiagnostics(settings).Check()));
    else if (setup) CertificateSetup.Create(settings.DataDirectory, settings.ListenAddress, settings.DirectOrigin.Length > 0 ? new Uri(settings.DirectOrigin).Host : null);
    else { new LocalStore(settings).RevokeAll(); Console.WriteLine("All device sessions revoked."); }
    return;
}
await using var app = RelayHost.Create(hostArgs);
app.Services.GetRequiredService<PairingCode>().Display();
await app.RunAsync();
