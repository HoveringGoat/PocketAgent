using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Win32;
using PocketAgent;
using PocketAgent.Desktop;

internal static class RelaySetupChecks
{
    public static void Run(string repository)
    {
        var previousDirectory = Environment.GetEnvironmentVariable("POCKETAGENT_DESKTOP_DATA");
        var shutdownMode = Application.Current.ShutdownMode;
        using var startup = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
        var startupValue = startup.GetValue("PocketAgent", null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        var startupKind = startupValue is null ? RegistryValueKind.Unknown : startup.GetValueKind("PocketAgent");
        Application.Current.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        var output = Path.Combine(repository, ".local", "desktop-relay-" + Guid.NewGuid().ToString("N"));
        try
        {
            Pump(CheckEnrollment(repository, output));
        }
        finally
        {
            if (startupValue is not null) startup.SetValue("PocketAgent", startupValue, startupKind);
            Environment.SetEnvironmentVariable("POCKETAGENT_DESKTOP_DATA", previousDirectory);
            Application.Current.ShutdownMode = shutdownMode;
        }
        Console.WriteLine("PASS desktop relay enable, saved enrollments, WSS round trips through both production servers, API authentication, disable and layout.");
    }

    private static async Task CheckEnrollment(string repository, string output)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(40));
        var cancellation = timeout.Token;
        var nodeDirectory = Path.Combine(output, "node");
        var remoteDirectory = Path.Combine(output, "remote");
        var node = OpenDesktop(repository, nodeDirectory);
        MainWindow? remote = null;
        try
        {
            var nodeMode = Control<ComboBox>(node, "RelayModeSetting");
            CheckMode(node, "off");
            nodeMode.SelectedValue = "host";
            CheckMode(node, "host");
            Click(node, "SaveButton");
            var savedNode = DesktopSettings.Load().Host.Relay;
            if (!savedNode.Enabled || !Guid.TryParse(savedNode.HostId, out _) || !Guid.TryParse(savedNode.ClientId, out var clientId) ||
                string.IsNullOrEmpty(savedNode.HostToken) || string.IsNullOrEmpty(savedNode.ClientToken))
                throw new InvalidOperationException("Saving Relay settings must persist an authenticated node enrollment.");
            Click(node, "ServerToggle");
            await WaitForServer(node, running: true, cancellation);
            if (nodeMode.IsEnabled || !Control<Button>(node, "CopyRelayEnrollmentButton").IsEnabled || !Control<Button>(node, "CopyRelayClientButton").IsEnabled ||
                Control<TextBlock>(node, "RelayServerOffNotice").Visibility != Visibility.Visible)
                throw new InvalidOperationException("Running relay controls must allow copying and lock configuration edits.");
            var enrollment = (RelayEnrollment)typeof(MainWindow).GetMethod("GetNodeEnrollment", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(node, null)!;
            if (!enrollment.RelayUrl.StartsWith("wss://", StringComparison.Ordinal) || enrollment.CertificatePin.Length != 64 ||
                savedNode.HostId != enrollment.HostId.ToString() || savedNode.NodeSessionKey != enrollment.SessionKey)
                throw new InvalidOperationException("Starting the node must preserve the saved credentials and export its managed WSS pin.");
            await CheckLayout(node, output, "host", cancellation);

            remote = OpenDesktop(repository, remoteDirectory);
            Control<ComboBox>(remote, "RelayModeSetting").SelectedValue = "client";
            CheckMode(remote, "client");
            var input = Control<PasswordBox>(remote, "RelayEnrollmentSetting");
            input.Password = "invalid enrollment";
            Click(remote, "SaveRelayEnrollmentButton");
            if (DesktopSettings.Load().Host.Relay.UpstreamUrl.Length > 0 || !Control<TextBlock>(remote, "ErrorDisplay").Text.Contains("pairing string", StringComparison.Ordinal))
                throw new InvalidOperationException("An invalid enrollment must show a validation error and leave the saved settings intact.");
            input.Password = enrollment.ToPairingString();
            Click(remote, "SaveRelayEnrollmentButton");
            remote.Close();
            remote = new MainWindow();
            CheckMode(remote, "client");
            var restored = DesktopSettings.Load().Host.Relay;
            if (restored.UpstreamUrl != enrollment.RelayUrl || restored.RemoteHostToken != enrollment.HostToken ||
                Control<FrameworkElement>(remote, "RelayEnrollmentPanel").Visibility != Visibility.Collapsed)
                throw new InvalidOperationException("Reopening the desktop must retain the enrollment and show its saved status.");
            Click(remote, "ReplaceRelayEnrollmentButton");
            if (Control<FrameworkElement>(remote, "RelayEnrollmentPanel").Visibility != Visibility.Visible)
                throw new InvalidOperationException("Replacing an enrollment must show a fresh paste field.");
            Control<PasswordBox>(remote, "RelayEnrollmentSetting").Password = "cancelled replacement";
            Click(remote, "ReplaceRelayEnrollmentButton");
            if (Control<FrameworkElement>(remote, "RelayEnrollmentPanel").Visibility != Visibility.Collapsed)
                throw new InvalidOperationException("Cancel replacement must return to the saved enrollment view.");
            Click(remote, "ServerToggle");
            await WaitForServer(remote, running: true, cancellation);
            await WaitUntil(() => Control<TextBlock>(remote, "RelayUpstreamStatus").Text == "Connected to relay.", cancellation);
            if (Control<FrameworkElement>(remote, "RelayEnrollmentPanel").Visibility != Visibility.Collapsed ||
                Control<Button>(remote, "UnenrollRelayButton").IsEnabled)
                throw new InvalidOperationException("A running client must show its status and explain how to unlock unenrollment.");
            await CheckRequests(remote, enrollment, clientId, savedNode.ClientToken, cancellation);

            Environment.SetEnvironmentVariable("POCKETAGENT_DESKTOP_DATA", nodeDirectory);
            Click(node, "ServerToggle");
            await WaitForServer(node, running: false, cancellation);
            await WaitUntil(() => Control<TextBlock>(remote, "RelayUpstreamStatus").Text.Contains("Retrying", StringComparison.Ordinal), cancellation);
            Click(node, "ServerToggle");
            await WaitForServer(node, running: true, cancellation);
            await WaitUntil(() => Control<TextBlock>(remote, "RelayUpstreamStatus").Text == "Connected to relay.", cancellation);
            await CheckRequests(remote, enrollment, clientId, savedNode.ClientToken, cancellation);

            Environment.SetEnvironmentVariable("POCKETAGENT_DESKTOP_DATA", remoteDirectory);
            await CheckLayout(remote, output, "connected", cancellation);
            Click(remote, "ServerToggle");
            await WaitForServer(remote, running: false, cancellation);
            await CheckLayout(remote, output, "saved", cancellation);
            Control<ComboBox>(remote, "RelayModeSetting").SelectedValue = "off";
            Click(remote, "SaveButton");
            var inactive = DesktopSettings.Load().Host.Relay;
            if (inactive.ConnectsUpstream || inactive.UpstreamUrl.Length == 0)
                throw new InvalidOperationException("Off must disable the upstream connection while retaining the saved enrollment.");
            Click(remote, "ServerToggle");
            await WaitForServer(remote, running: true, cancellation);
            var running = (WebApplication)typeof(MainWindow).GetField("relay", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(remote)!;
            var upstream = running.Services.GetRequiredService<RelayRemoteHostService>();
            if (!upstream.ExecuteTask!.IsCompletedSuccessfully || upstream.Status != RelayConnectionStatus.Stopped)
                throw new InvalidOperationException("An inactive saved enrollment must leave the outbound service stopped.");
            Click(remote, "ServerToggle");
            await WaitForServer(remote, running: false, cancellation);
            Control<ComboBox>(remote, "RelayModeSetting").SelectedValue = "client";
            if (!Control<Button>(remote, "UnenrollRelayButton").IsEnabled)
                throw new InvalidOperationException("A stopped client must allow unenrollment.");
            Click(remote, "UnenrollRelayButton");
            var unenrolled = DesktopSettings.Load().Host.Relay;
            using var enrollmentField = Control<PasswordBox>(remote, "RelayEnrollmentSetting").SecurePassword;
            if (!unenrolled.ConnectsUpstream || unenrolled.UpstreamUrl.Length != 0 || unenrolled.RemoteHostToken.Length != 0 ||
                unenrolled.SessionKey.Length != 0 || enrollmentField.Length != 0)
                throw new InvalidOperationException("Unenroll must remove stored credentials while leaving Connect to relay selected.");
            CheckMode(remote, "client");
            await CheckLayout(remote, output, "unenrolled", cancellation);
            remote.Close();
            remote = new MainWindow();
            CheckMode(remote, "client");

            Environment.SetEnvironmentVariable("POCKETAGENT_DESKTOP_DATA", nodeDirectory);
            Click(node, "ServerToggle");
            await WaitForServer(node, running: false, cancellation);
            nodeMode.SelectedValue = "off";
            CheckMode(node, "off");
            Click(node, "SaveButton");
            if (DesktopSettings.Load().Host.Relay.Enabled)
                throw new InvalidOperationException("Unchecking the relay node must disable it on restart.");
            Click(node, "ServerToggle");
            await WaitForServer(node, running: true, cancellation);
            await CheckLayout(node, output, "off", cancellation);
            using var probe = new System.Net.Http.HttpClient(new System.Net.Http.HttpClientHandler
            {
                ServerCertificateCustomValidationCallback = (_, certificate, _, _) =>
                    certificate is not null && PeerCertificates.Fingerprint(certificate) == enrollment.CertificatePin
            });
            var disabledEndpoint = new UriBuilder(enrollment.RelayUrl) { Scheme = "https" }.Uri;
            using var disabled = await probe.GetAsync(disabledEndpoint, cancellation);
            if (disabled.StatusCode != HttpStatusCode.NotFound)
                throw new InvalidOperationException("Disabling the relay node must remove its WebSocket endpoint.");
        }
        finally
        {
            remote?.Close();
            var shutdown = (Task)typeof(MainWindow).GetMethod("Shutdown", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(node, null)!;
            await shutdown;
            node.Close();
        }
    }

    private static void CheckMode(MainWindow window, string expected)
    {
        if (Control<ComboBox>(window, "RelayModeSetting").SelectedValue as string != expected ||
            Control<FrameworkElement>(window, "RelayHostPanel").Visibility != (expected == "host" ? Visibility.Visible : Visibility.Collapsed) ||
            Control<FrameworkElement>(window, "RelayClientPanel").Visibility != (expected == "client" ? Visibility.Visible : Visibility.Collapsed))
            throw new InvalidOperationException($"Relay mode {expected} must show only the selected role.");
    }

    private static MainWindow OpenDesktop(string repository, string dataDirectory)
    {
        Environment.SetEnvironmentVariable("POCKETAGENT_DESKTOP_DATA", dataDirectory);
        var settings = new DesktopSettings { StartServerOnLaunch = false, CheckUpdatesOnStartup = false, CheckUpdatesPeriodically = false };
        settings.Host.RepositoryPath = repository;
        settings.Host.CodexExecutable = Path.Combine(dataDirectory, "unused-codex.exe");
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        settings.Host.Port = ((IPEndPoint)listener.LocalEndpoint).Port;
        settings.Host.DataDirectory = dataDirectory;
        settings.Host.PinnedCertificate = true;
        settings.Host.CertificatePath = Path.Combine(dataDirectory, "certificates", "device", "server.pfx");
        settings.Save();
        return new MainWindow();
    }

    private static async Task CheckRequests(MainWindow remote, RelayEnrollment enrollment, Guid clientId, string clientToken, CancellationToken cancellation)
    {
        if (!Control<Button>(remote, "CopyRelayDeviceTicketButton").IsEnabled)
            throw new InvalidOperationException("The connected remote desktop must offer a device pairing ticket.");
        var ticket = (string)typeof(MainWindow).GetMethod("GetRelayDeviceTicket", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(remote, null)!;
        await CheckRejectedConnection(enrollment, clientId, clientToken, new string('0', 64), cancellation);
        await CheckRejectedConnection(enrollment, clientId, "invalid-client-credential", enrollment.CertificatePin, cancellation);
        await using var client = await RelayApplicationClient.ConnectAsync(new Uri(enrollment.RelayUrl), enrollment.HostId, clientId,
            clientToken, RelaySessionKey.Parse(enrollment.SessionKey), cancellation, enrollment.CertificatePin);
        var pairRequest = RelayApplicationRequest.Create("POST", "/api/devices/pair",
            JsonSerializer.SerializeToUtf8Bytes(new { deviceName = "Relay acceptance device", code = ticket })) with
            { Headers = [new("Content-Type", "application/json")] };
        var pairResponse = await client.RequestAsync(pairRequest, cancellation);
        if (pairResponse.Status != 200)
            throw new InvalidOperationException($"Pairing through the relay failed with status {pairResponse.Status}.");
        using var pairing = JsonDocument.Parse(pairResponse.Body);
        var deviceToken = pairing.RootElement.GetProperty("token").GetString();
        var request = RelayApplicationRequest.Create("GET", "/api/session", []);
        var authenticated = await client.RequestAsync(request with { Headers = [new("Authorization", "Bearer " + deviceToken)] }, cancellation);
        var unauthenticated = await client.RequestAsync(RelayApplicationRequest.Create("GET", "/api/scheduled", []), cancellation);
        var external = await client.RequestAsync(RelayApplicationRequest.Create("GET", "https://127.0.0.1/api/session", []), cancellation);
        using var body = JsonDocument.Parse(authenticated.Body);
        if (authenticated.Status != 200 || body.RootElement.GetProperty("deviceName").GetString() != "Relay acceptance device" ||
            unauthenticated.Status != 401 || external.Status != 400)
            throw new InvalidOperationException("The production relay must reach the authenticated API on the remote node and keep local API boundaries.");
    }

    private static async Task CheckRejectedConnection(RelayEnrollment enrollment, Guid clientId, string token, string pin, CancellationToken cancellation)
    {
        try
        {
            await using var connection = await RelayTransportClient.ConnectAsync(new Uri(enrollment.RelayUrl), false, clientId, token, cancellation, pin);
        }
        catch (WebSocketException) when (pin != enrollment.CertificatePin) { return; }
        catch (InvalidOperationException exception) when (pin == enrollment.CertificatePin && exception.Message == "Relay authentication failed.") { return; }
        throw new InvalidOperationException("The live relay accepted an invalid TLS pin or client credential.");
    }

    private static async Task CheckLayout(MainWindow window, string output, string state, CancellationToken cancellation)
    {
        window.ShowActivated = false;
        Control<TabControl>(window, "Navigation").SelectedIndex = 2;
        Control<ComboBox>(window, "UiScaleSetting").SelectedValue = "large";
        if (!window.IsVisible) window.Show();
        await Task.Delay(200, cancellation);
        var section = Control<FrameworkElement>(window, "RelaySetupSection");
        var status = Control<TextBlock>(window, "RelayNodeStatus");
        if (state == "host")
            status.Text = "Relay node listening at wss://" + string.Join(".", Enumerable.Repeat(new string('w', 59), 4)) + "/relay";
        ((Run)window.FindName("ConversationLocation")!).Text = @"C:\GitHub\" + new string('W', 180);
        foreach (var (width, height) in new[] { (window.MinWidth, window.MinHeight), (1150d, 820d) })
        {
            window.Width = width;
            window.Height = height;
            window.UpdateLayout();
            var scroller = (ScrollViewer)((TabItem)Control<TabControl>(window, "Navigation").SelectedItem).Content;
            var sectionTop = section.TransformToAncestor(scroller).Transform(new Point()).Y;
            scroller.ScrollToVerticalOffset(scroller.VerticalOffset + sectionTop - 8);
            window.UpdateLayout();
            var viewport = (ScrollContentPresenter)scroller.Template.FindName("PART_ScrollContentPresenter", scroller);
            var primaryControls = new[] { "RelayModeSetting", "RelayServerOffNotice", "RelayEnrollmentSetting", "RelayNodeStatus", "RelayUpstreamStatus",
                "CopyRelayEnrollmentButton", "UnenrollRelayButton", "SaveRelayEnrollmentButton", "ReplaceRelayEnrollmentButton" };
            foreach (var name in primaryControls)
            {
                var control = Control<FrameworkElement>(window, name);
                if (!control.IsVisible) continue;
                var top = control.TransformToAncestor(viewport).Transform(new Point()).Y;
                if (top < 0 || top + control.ActualHeight > viewport.ActualHeight)
                {
                    control.BringIntoView();
                    window.UpdateLayout();
                }
                var bounds = control.TransformToAncestor(viewport).TransformBounds(new Rect(control.RenderSize));
                if (!control.IsVisible || bounds.Width <= 0 || bounds.Height <= 0 || bounds.Left < 0 || bounds.Top < 0 ||
                    bounds.Right > viewport.ActualWidth + 0.5 || bounds.Bottom > viewport.ActualHeight + 0.5)
                    throw new InvalidOperationException($"Relay Settings control {name} is outside the scroll viewport at {width} × {height}, large scale. Bounds {bounds}, viewport {viewport.RenderSize}.");
            }
            var root = Control<Grid>(window, "RootLayout");
            var header = new[] { "SessionsNavButton", "DevicesNavButton", "SettingsNavButton", "ServerToggle" };
            foreach (var name in header)
            {
                var control = Control<FrameworkElement>(window, name);
                if (System.Windows.Controls.Primitives.LayoutInformation.GetLayoutClip(control) is not null ||
                    !new Rect(root.RenderSize).Contains(control.TransformToAncestor(root).TransformBounds(new Rect(control.RenderSize))))
                    throw new InvalidOperationException($"The Settings header clips {name} at {width} × {height}, large scale.");
            }
            if (state == "host" && status.ActualHeight <= status.FontSize * 2)
                throw new InvalidOperationException("A long relay endpoint must wrap in Settings.");
            scroller.ScrollToVerticalOffset(scroller.VerticalOffset + section.TransformToAncestor(scroller).Transform(new Point()).Y - 8);
            window.UpdateLayout();
            var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(window);
            var png = new PngBitmapEncoder();
            png.Frames.Add(BitmapFrame.Create(bitmap));
            using var file = File.Create(Path.Combine(output, $"relay-{state}-{width}-{height}.png"));
            png.Save(file);
        }
    }

    private static T Control<T>(MainWindow window, string name) where T : FrameworkElement =>
        (T?)window.FindName(name) ?? throw new InvalidOperationException($"Desktop Settings must expose {name}.");

    private static void Click(MainWindow window, string name) =>
        Control<ButtonBase>(window, name).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));

    private static async Task WaitForServer(MainWindow window, bool running, CancellationToken cancellation)
    {
        var toggle = Control<ToggleButton>(window, "ServerToggle");
        await WaitUntil(() => toggle.IsEnabled && !Equals(toggle.Content, "Starting...") && !Equals(toggle.Content, "Stopping..."), cancellation);
        if (toggle.IsChecked != running)
            throw new InvalidOperationException($"Desktop server failed to {(running ? "start" : "stop")}. {Control<TextBlock>(window, "ErrorDisplay").Text}");
    }

    private static async Task WaitUntil(Func<bool> condition, CancellationToken cancellation)
    {
        while (!condition()) await Task.Delay(50, cancellation);
    }

    private static void Pump(Task work)
    {
        var frame = new DispatcherFrame();
        var dispatcher = Dispatcher.CurrentDispatcher;
        _ = work.ContinueWith(_ => dispatcher.BeginInvoke(() => frame.Continue = false), TaskScheduler.Default);
        Dispatcher.PushFrame(frame);
        work.GetAwaiter().GetResult();
    }
}
