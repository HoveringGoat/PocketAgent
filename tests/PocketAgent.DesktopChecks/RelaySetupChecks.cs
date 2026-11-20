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
            var enable = Control<CheckBox>(node, "RelayNodeSetting");
            enable.IsChecked = true;
            Click(node, "SaveButton");
            var savedNode = DesktopSettings.Load().Host.Relay;
            if (!savedNode.Enabled || !Guid.TryParse(savedNode.HostId, out _) || !Guid.TryParse(savedNode.ClientId, out var clientId) ||
                string.IsNullOrEmpty(savedNode.HostToken) || string.IsNullOrEmpty(savedNode.ClientToken))
                throw new InvalidOperationException("Saving Relay settings must persist an authenticated node enrollment.");
            Click(node, "ServerToggle");
            await WaitForServer(node, running: true, cancellation);
            if (enable.IsEnabled || !Control<Button>(node, "CopyRelayEnrollmentButton").IsEnabled || !Control<Button>(node, "CopyRelayClientButton").IsEnabled)
                throw new InvalidOperationException("Running relay controls must allow copying and lock configuration edits.");
            var enrollment = (RelayEnrollment)typeof(MainWindow).GetMethod("GetNodeEnrollment", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(node, null)!;
            if (!enrollment.RelayUrl.StartsWith("wss://", StringComparison.Ordinal) || enrollment.CertificatePin.Length != 64 ||
                savedNode.HostId != enrollment.HostId.ToString() || savedNode.NodeSessionKey != enrollment.SessionKey)
                throw new InvalidOperationException("Starting the node must preserve the saved credentials and export its managed WSS pin.");

            remote = OpenDesktop(repository, remoteDirectory);
            var input = Control<PasswordBox>(remote, "RelayEnrollmentSetting");
            input.Password = "invalid enrollment";
            Click(remote, "SaveButton");
            if (DesktopSettings.Load().Host.Relay.UpstreamUrl.Length > 0 || !Control<TextBlock>(remote, "ErrorDisplay").Text.Contains("pairing string", StringComparison.Ordinal))
                throw new InvalidOperationException("An invalid enrollment must show a validation error and leave the saved settings intact.");
            input.Password = enrollment.ToPairingString();
            Control<CheckBox>(remote, "RelayNodeSetting").IsChecked = true;
            Click(remote, "SaveButton");
            remote.Close();
            remote = new MainWindow();
            if (Control<PasswordBox>(remote, "RelayEnrollmentSetting").Password != enrollment.ToPairingString())
                throw new InvalidOperationException("The upstream enrollment must survive reopening the desktop.");
            Click(remote, "ServerToggle");
            await WaitForServer(remote, running: true, cancellation);
            await WaitUntil(() => Control<TextBlock>(remote, "RelayUpstreamStatus").Text == "Upstream relay connected.", cancellation);
            await CheckRequests(remote, enrollment, clientId, savedNode.ClientToken, cancellation);

            Environment.SetEnvironmentVariable("POCKETAGENT_DESKTOP_DATA", nodeDirectory);
            Click(node, "ServerToggle");
            await WaitForServer(node, running: false, cancellation);
            await WaitUntil(() => Control<TextBlock>(remote, "RelayUpstreamStatus").Text.Contains("Retrying", StringComparison.Ordinal), cancellation);
            Click(node, "ServerToggle");
            await WaitForServer(node, running: true, cancellation);
            await WaitUntil(() => Control<TextBlock>(remote, "RelayUpstreamStatus").Text == "Upstream relay connected.", cancellation);
            await CheckRequests(remote, enrollment, clientId, savedNode.ClientToken, cancellation);

            Environment.SetEnvironmentVariable("POCKETAGENT_DESKTOP_DATA", remoteDirectory);
            await CheckLayout(remote, output, cancellation);
            Click(remote, "ServerToggle");
            await WaitForServer(remote, running: false, cancellation);
            Control<PasswordBox>(remote, "RelayEnrollmentSetting").Clear();
            Click(remote, "SaveButton");
            if (DesktopSettings.Load().Host.Relay.UpstreamUrl.Length != 0)
                throw new InvalidOperationException("Clearing the enrollment must disable the outbound connection on restart.");

            Environment.SetEnvironmentVariable("POCKETAGENT_DESKTOP_DATA", nodeDirectory);
            Click(node, "ServerToggle");
            await WaitForServer(node, running: false, cancellation);
            enable.IsChecked = false;
            Click(node, "SaveButton");
            if (DesktopSettings.Load().Host.Relay.Enabled)
                throw new InvalidOperationException("Unchecking the relay node must disable it on restart.");
            Click(node, "ServerToggle");
            await WaitForServer(node, running: true, cancellation);
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

    private static async Task CheckLayout(MainWindow window, string output, CancellationToken cancellation)
    {
        window.ShowActivated = false;
        Control<TabControl>(window, "Navigation").SelectedIndex = 2;
        Control<ComboBox>(window, "UiScaleSetting").SelectedValue = "large";
        window.Show();
        await Task.Delay(200, cancellation);
        var section = Control<FrameworkElement>(window, "RelaySetupSection");
        var status = Control<TextBlock>(window, "RelayNodeStatus");
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
            foreach (var name in new[] { "RelaySetupSection", "RelayNodeSetting", "RelayEnrollmentSetting", "RelayNodeStatus", "RelayUpstreamStatus", "CopyRelayEnrollmentButton", "CopyRelayClientButton", "CopyRelayDeviceTicketButton" })
            {
                var control = Control<FrameworkElement>(window, name);
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
            if (status.ActualHeight <= status.FontSize * 2)
                throw new InvalidOperationException("A long relay endpoint must wrap in Settings.");
            var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(window);
            var png = new PngBitmapEncoder();
            png.Frames.Add(BitmapFrame.Create(bitmap));
            using var file = File.Create(Path.Combine(output, $"relay-settings-{width}-{height}.png"));
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
