using System.Text.Json;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;

namespace PocketAgent.Desktop;

public partial class MainWindow
{
    private void LoadRelaySetup()
    {
        var saved = settings.Host.Relay;
        RelayNodeSetting.IsChecked = saved.Enabled;
        if (saved.UpstreamUrl.Length > 0 && Guid.TryParse(saved.RemoteHostId, out var hostId))
            RelayEnrollmentSetting.Password = new RelayEnrollment(saved.UpstreamUrl, hostId, saved.RemoteHostToken,
                saved.SessionKey, saved.UpstreamCertificatePin).ToPairingString();
        UpdateRelaySetup();
    }

    private RelaySettings ReadRelaySettings()
    {
        var saved = settings.Host.Relay;
        var pasted = RelayEnrollmentSetting.Password.Trim();
        var upstream = pasted.Length > 0 ? RelayEnrollment.Parse(pasted) : null;
        var node = new RelaySettings
        {
            Enabled = RelayNodeSetting.IsChecked == true,
            HostId = saved.HostId,
            HostToken = saved.HostToken,
            ClientId = saved.ClientId,
            ClientToken = saved.ClientToken,
            NodeSessionKey = saved.NodeSessionKey,
            UpstreamUrl = upstream?.RelayUrl ?? "",
            RemoteHostId = upstream?.HostId.ToString() ?? "",
            RemoteHostToken = upstream?.HostToken ?? "",
            SessionKey = upstream?.SessionKey ?? "",
            UpstreamCertificatePin = upstream?.CertificatePin ?? ""
        };
        if (node.Enabled)
        {
            if (node.HostId.Length == 0) node.HostId = Guid.NewGuid().ToString();
            if (node.HostToken.Length == 0) node.HostToken = RelayEnrollment.CreateToken();
            if (node.ClientId.Length == 0) node.ClientId = Guid.NewGuid().ToString();
            if (node.ClientToken.Length == 0) node.ClientToken = RelayEnrollment.CreateToken();
            if (node.NodeSessionKey.Length == 0) node.NodeSessionKey = RelaySessionKey.Create().ToWire();
            if (!Guid.TryParse(node.ClientId, out var clientId) || clientId == Guid.Empty)
                throw new InvalidOperationException("The saved relay client identity is invalid.");
            _ = RelaySessionKey.Parse(node.NodeSessionKey);
        }
        return node;
    }

    private static Uri RelayNodeEndpoint(HostSettings host)
    {
        var origin = new Uri(host.ClientOrigin);
        return new UriBuilder(origin) { Scheme = origin.Scheme == "https" ? "wss" : "ws", Path = "/relay" }.Uri;
    }

    private RelayEnrollment GetNodeEnrollment()
    {
        var host = settings.Host;
        if (relay is null || !host.Relay.Enabled)
            throw new InvalidOperationException("Start the server with the relay node enabled to copy enrollment.");
        var pin = host.UseHttps && host.PublicOrigin.Length == 0
            ? PeerCertificates.Fingerprint(relay.Services.GetRequiredService<PeerCertificates>().Current)
            : "";
        return new(RelayNodeEndpoint(host).AbsoluteUri, Guid.Parse(host.Relay.HostId), host.Relay.HostToken,
            host.Relay.NodeSessionKey, pin);
    }

    private void UpdateRelaySetup()
    {
        var host = settings.Host;
        var nodeRunning = relay is not null && host.Relay.Enabled;
        CopyRelayEnrollmentButton.IsEnabled = CopyRelayClientButton.IsEnabled = nodeRunning;
        RelayNodeStatus.Text = nodeRunning ? $"Relay node listening at {RelayNodeEndpoint(host).AbsoluteUri}" :
            host.Relay.Enabled ? "Relay node enabled. Start the server to listen." : "Relay node off.";
        var status = relay?.Services.GetRequiredService<RelayRemoteHostService>().Status;
        CopyRelayDeviceTicketButton.Visibility = host.Relay.UpstreamUrl.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        CopyRelayDeviceTicketButton.IsEnabled = status == RelayConnectionStatus.Connected;
        RelayUpstreamStatus.Text = status switch
        {
            RelayConnectionStatus.Connected => "Upstream relay connected.",
            RelayConnectionStatus.Connecting => "Connecting to upstream relay…",
            RelayConnectionStatus.Retrying => "Upstream relay unavailable. Retrying…",
            _ => host.Relay.UpstreamUrl.Length > 0 ? "Upstream relay saved. Start the server to connect." : "No upstream relay configured."
        };
    }

    private async void CopyRelayEnrollment(object sender, RoutedEventArgs e) => await Act(() =>
    {
        Clipboard.SetText(GetNodeEnrollment().ToPairingString());
        ErrorDisplay.Text = "Host enrollment copied. Share it privately.";
        return Task.CompletedTask;
    });

    private async void CopyRelayClientSetup(object sender, RoutedEventArgs e) => await Act(() =>
    {
        var enrollment = GetNodeEnrollment();
        Clipboard.SetText(JsonSerializer.Serialize(new
        {
            relayUrl = enrollment.RelayUrl,
            hostId = enrollment.HostId,
            clientId = settings.Host.Relay.ClientId,
            clientToken = settings.Host.Relay.ClientToken,
            sessionKey = enrollment.SessionKey,
            certificatePin = enrollment.CertificatePin
        }));
        ErrorDisplay.Text = "Client setup copied. Share it privately.";
        return Task.CompletedTask;
    });

    private string GetRelayDeviceTicket()
    {
        if (relay?.Services.GetRequiredService<RelayRemoteHostService>().Status != RelayConnectionStatus.Connected)
            throw new InvalidOperationException("Connect to the upstream relay before copying a device pairing ticket.");
        var pairing = relay.Services.GetRequiredService<PairingCode>();
        var current = pairing.Current;
        if (current.Consumed || current.ExpiresAt <= DateTimeOffset.UtcNow) pairing.Renew();
        return pairing.Ticket;
    }

    private async void CopyRelayDeviceTicket(object sender, RoutedEventArgs e) => await Act(() =>
    {
        Clipboard.SetText(GetRelayDeviceTicket());
        ErrorDisplay.Text = "Device pairing ticket copied. Share it privately.";
        return Task.CompletedTask;
    });
}
