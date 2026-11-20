using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Extensions.DependencyInjection;

namespace PocketAgent.Desktop;

public partial class MainWindow
{
    private bool CanEditRelaySettings => relay is null && ServerToggle.IsEnabled;
    private bool replacingRelayEnrollment;

    private static HostSettings RuntimeHostSettings(HostSettings host) =>
        host.Relay.ConnectsUpstream && !host.Relay.Enabled ? host.WithLoopbackHttpListener() : host;

    private void LoadRelaySetup()
    {
        var saved = settings.Host.Relay;
        RelayModeSetting.SelectedValue = saved.Enabled ? "host" : saved.ConnectsUpstream ? "client" : "off";
        UpdateRelaySetup();
    }

    private RelaySettings ReadRelaySettings(bool removeEnrollment)
    {
        var saved = settings.Host.Relay;
        var mode = RelayModeSetting.SelectedValue as string;
        var connecting = mode == "client";
        var pasted = connecting && !removeEnrollment ? RelayEnrollmentSetting.Password.Trim() : "";
        var upstream = pasted.Length > 0 ? RelayEnrollment.Parse(pasted) : null;
        var previous = removeEnrollment ? new RelaySettings() : saved;
        var node = new RelaySettings
        {
            Enabled = mode == "host",
            UpstreamEnabled = connecting,
            HostId = saved.HostId,
            HostToken = saved.HostToken,
            ClientId = saved.ClientId,
            ClientToken = saved.ClientToken,
            NodeSessionKey = saved.NodeSessionKey,
            UpstreamUrl = upstream?.RelayUrl ?? previous.UpstreamUrl,
            RemoteHostId = upstream?.HostId.ToString() ?? previous.RemoteHostId,
            RemoteHostToken = upstream?.HostToken ?? previous.RemoteHostToken,
            SessionKey = upstream?.SessionKey ?? previous.SessionKey,
            UpstreamCertificatePin = upstream?.CertificatePin ?? previous.UpstreamCertificatePin
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
        var mode = RelayModeSetting.SelectedValue as string;
        var editing = CanEditRelaySettings;
        var host = settings.Host;
        var nodeRunning = relay is not null && host.Relay.Enabled;
        RelayModeSetting.IsEnabled = editing;
        RelayServerOffNotice.Visibility = editing ? Visibility.Collapsed : Visibility.Visible;
        RelayHostPanel.Visibility = mode == "host" ? Visibility.Visible : Visibility.Collapsed;
        RelayClientPanel.Visibility = mode == "client" ? Visibility.Visible : Visibility.Collapsed;
        CopyRelayEnrollmentButton.IsEnabled = CopyRelayClientButton.IsEnabled = nodeRunning;
        var endpoint = nodeRunning ? RelayNodeEndpoint(host) : null;
        RelayNodeAddressHint.Visibility = endpoint?.IsLoopback == true ? Visibility.Visible : Visibility.Collapsed;
        RelayNodeStatus.Text = endpoint is not null ? $"Relay node listening at {endpoint.AbsoluteUri}" :
            host.Relay.Enabled ? "Relay node saved. Turn the server ON to start hosting." : "Save settings, then turn the server ON to start hosting.";
        var status = relay?.Services.GetRequiredService<RelayRemoteHostService>().Status;
        var enrolled = host.Relay.UpstreamUrl.Length > 0;
        var showEnrollment = editing && (!enrolled || replacingRelayEnrollment);
        RelayEnrollmentPanel.Visibility = SaveRelayEnrollmentButton.Visibility = showEnrollment ? Visibility.Visible : Visibility.Collapsed;
        ReplaceRelayEnrollmentButton.Visibility = editing && enrolled ? Visibility.Visible : Visibility.Collapsed;
        ReplaceRelayEnrollmentButton.Content = replacingRelayEnrollment ? "Cancel replacement" : "Replace enrollment";
        using var enrollmentInput = showEnrollment && mode == "client" ? RelayEnrollmentSetting.SecurePassword : null;
        SaveRelayEnrollmentButton.IsEnabled = enrollmentInput is { Length: > 0 };
        UnenrollRelayButton.Visibility = enrolled ? Visibility.Visible : Visibility.Collapsed;
        UnenrollRelayButton.IsEnabled = editing && enrolled;
        RelayClientAdvanced.Visibility = status == RelayConnectionStatus.Connected ? Visibility.Visible : Visibility.Collapsed;
        CopyRelayDeviceTicketButton.IsEnabled = status == RelayConnectionStatus.Connected;
        RelayUpstreamStatus.Text = status switch
        {
            RelayConnectionStatus.Connected => "Connected to relay.",
            RelayConnectionStatus.Connecting => "Connecting to relay…",
            RelayConnectionStatus.Retrying => "Relay unavailable. Retrying…",
            _ when replacingRelayEnrollment && editing => "Paste a new enrollment to replace the saved one.",
            _ when enrolled => "Enrollment saved. Turn the server ON to connect.",
            _ when editing => "Paste an enrollment to connect to a relay.",
            _ => "No enrollment saved. Turn the server OFF to enroll."
        };
    }

    private void RelayModeChanged(object sender, SelectionChangedEventArgs e)
    {
        replacingRelayEnrollment = false;
        RelayEnrollmentSetting.Clear();
        UpdateRelaySetup();
    }
    private void RelayEnrollmentChanged(object sender, RoutedEventArgs e) => UpdateRelaySetup();

    private void ReplaceRelayEnrollment(object sender, RoutedEventArgs e)
    {
        replacingRelayEnrollment = !replacingRelayEnrollment;
        if (!replacingRelayEnrollment) RelayEnrollmentSetting.Clear();
        UpdateRelaySetup();
        if (replacingRelayEnrollment) RelayEnrollmentSetting.Focus();
    }

    private async void SaveRelayEnrollment(object sender, RoutedEventArgs e) => await Act(() =>
    {
        if (!CanEditRelaySettings) throw new InvalidOperationException("Turn the server OFF to save enrollment.");
        if (string.IsNullOrWhiteSpace(RelayEnrollmentSetting.Password))
            throw new InvalidOperationException("Paste the client enrollment first.");
        SaveConfiguration();
        return Task.CompletedTask;
    });

    private async void UnenrollRelay(object sender, RoutedEventArgs e) => await Act(() =>
    {
        if (!CanEditRelaySettings) throw new InvalidOperationException("Turn the server OFF to unenroll.");
        SaveConfiguration(removeRelayEnrollment: true);
        return Task.CompletedTask;
    });

    private async void CopyRelayEnrollment(object sender, RoutedEventArgs e) => await Act(() =>
    {
        Clipboard.SetText(GetNodeEnrollment().ToPairingString());
        ErrorDisplay.Text = "Client enrollment copied. Paste it on the other desktop.";
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
        ErrorDisplay.Text = ".NET client credentials copied. Share them privately.";
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

    private string GetRelayPhonePairingPayload()
    {
        if (relay?.Services.GetRequiredService<RelayRemoteHostService>().SupportsPhonePairing != true)
            throw new InvalidOperationException("Connect to a relay node that supports phone pairing.");
        var upstream = settings.Host.Relay;
        if (!upstream.UpstreamUrl.StartsWith("wss://", StringComparison.Ordinal))
            throw new InvalidOperationException("Phone pairing requires HTTPS on the relay node.");
        var enrollment = new RelayEnrollment(upstream.UpstreamUrl, Guid.Parse(upstream.RemoteHostId), upstream.RemoteHostToken,
            upstream.SessionKey, upstream.UpstreamCertificatePin);
        return enrollment.ToPhonePairingString(relay.Services.GetRequiredService<PairingCode>().Ticket);
    }

    private async void CopyRelayDeviceTicket(object sender, RoutedEventArgs e) => await Act(() =>
    {
        Clipboard.SetText(GetRelayDeviceTicket());
        ErrorDisplay.Text = "Device pairing ticket copied. Share it privately.";
        return Task.CompletedTask;
    });
}
