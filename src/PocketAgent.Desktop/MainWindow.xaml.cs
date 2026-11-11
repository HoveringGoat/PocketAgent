using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Win32;

namespace PocketAgent.Desktop;

public partial class MainWindow : Window
{
    // Keep a large or tool-heavy conversation from making the WPF visual tree
    // unbounded while a turn is streaming. The complete history remains in
    // Codex; this is only a rendering budget for the desktop view.
    private const int MaxRenderedItems = 400;
    private const int MaxRenderedCharacters = 300_000;
    private DesktopSettings settings = new();
    private WebApplication? relay;
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly Dictionary<string, string> drafts = [];
    private ConversationSnapshot? conversation;
    private string? selected;
    private string rendered = "";
    private string sessionSignature = "";
    private string qrRendered = "";
    private bool refreshing, sending, settingDraft, changingList, closing, transitioning;
    private Conversations Conversations => relay!.Services.GetRequiredService<Conversations>();

    public MainWindow()
    {
        InitializeComponent();
        Title = $"PocketAgent Server {BuildInfo.Version}";
        BuildDisplay.Text = $"Desktop {BuildInfo.Version}\n{Environment.ProcessPath}";
        try { settings = DesktopSettings.Load(); }
        catch (Exception) { ErrorDisplay.Text = "Saved settings could not be read. Review and save the setup again."; }
        RepositorySetting.Text = settings.Host.RepositoryPath;
        var arguments = Environment.GetCommandLineArgs();
        var repositoryArgument = Array.IndexOf(arguments, "--repository");
        if (RepositorySetting.Text.Length == 0 && repositoryArgument >= 0 && repositoryArgument + 1 < arguments.Length)
            RepositorySetting.Text = arguments[repositoryArgument + 1];
        CodexSetting.Text = settings.Host.CodexExecutable;
        PermissionModeSetting.SelectedValue = settings.Host.PermissionMode;
        AddressSetting.Text = settings.Host.ListenAddress;
        PortSetting.Text = settings.Host.Port.ToString(System.Globalization.CultureInfo.InvariantCulture);
        CertificateSetting.Text = settings.Host.CertificatePath;
        DirectOriginSetting.Text = settings.Host.DirectOrigin;
        if (settings.Host.RepositoryPath.Length == 0) Navigation.SelectedIndex = 2;
        ResolveCodex();
        timer.Tick += async (_, _) => await Refresh();
        timer.Start();
    }

    private void ResolveCodex()
    {
        try { var executable = CodexExecutableResolver.Resolve(settings.Host); CodexResolved.Text = $"{executable.Source}\n{executable.Path}"; }
        catch (Exception exception) { CodexResolved.Text = exception.Message; }
    }

    private async Task Act(Func<Task> work, bool clearError = true)
    {
        if (clearError) ErrorDisplay.Text = "";
        try { await work(); }
        catch (ApiException exception) { ErrorDisplay.Text = exception.Message; }
        catch (Exception exception) when (exception is InvalidOperationException or IOException or UnauthorizedAccessException or FormatException or System.Net.Sockets.SocketException)
        { ErrorDisplay.Text = exception.Message; }
        catch (Exception) { ErrorDisplay.Text = "The operation failed. Check the local configuration and restart if needed."; }
    }

    private void SaveConfiguration()
    {
        if (relay is not null) throw new InvalidOperationException("Stop the server before changing settings.");
        var candidate = new DesktopSettings
        {
            Host = new HostSettings
            {
                RepositoryPath = RepositorySetting.Text.Trim(),
                CodexExecutable = CodexSetting.Text.Trim(),
                CodexArguments = settings.Host.CodexArguments,
                CodexExtensionsDirectory = settings.Host.CodexExtensionsDirectory,
                ListenAddress = AddressSetting.Text.Trim(),
                Port = int.Parse(PortSetting.Text, System.Globalization.CultureInfo.InvariantCulture),
                CertificatePath = CertificateSetting.Text.Trim(),
                PublicOrigin = "",
                DirectOrigin = DirectOriginSetting.Text.Trim(),
                PinnedCertificate = settings.Host.PinnedCertificate && CertificateSetting.Text.Trim() == settings.Host.CertificatePath,
                PermissionMode = PermissionModeSetting.SelectedValue as string ?? "inherit",
                DataDirectory = DesktopSettings.DirectoryPath
            }
        };
        candidate.Host.Validate();
        candidate.Save(); settings = candidate; ResolveCodex();
    }

    private async void SaveSettings(object sender, RoutedEventArgs e) => await Act(() => { SaveConfiguration(); ErrorDisplay.Text = "Settings saved."; return Task.CompletedTask; });
    private void BrowseRepository(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFolderDialog { Title = "Choose the repository Codex will work in" };
        if (picker.ShowDialog(this) == true) RepositorySetting.Text = picker.FolderName;
    }
    private async void SetupWifi(object sender, RoutedEventArgs e) => await Act(async () =>
    {
        if (relay is not null) throw new InvalidOperationException("Stop the server before applying Wi-Fi setup. Active work will end when the server stops.");
        var addresses = PrivateInterfaceLabels();
        if (addresses.Length == 0) throw new InvalidOperationException("No private IPv4 address was found. Connect the desktop to Wi-Fi or Ethernet first.");
        var choices = new ComboBox { ItemsSource = addresses, SelectedIndex = 0, Margin = new Thickness(16) };
        var apply = new Button { Content = "Create certificate and use this address", Margin = new Thickness(16) };
        var panel = new StackPanel(); panel.Children.Add(new TextBlock { Text = "Choose the network shared with your phone. No firewall or router settings will be changed.", Margin = new Thickness(16) }); panel.Children.Add(choices); panel.Children.Add(apply);
        var dialog = new Window { Title = "Wi-Fi HTTPS setup", Owner = this, Width = 560, SizeToContent = SizeToContent.Height, Content = panel, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        apply.Click += (_, _) => dialog.DialogResult = true;
        if (dialog.ShowDialog() != true) return;
        var address = ((string)choices.SelectedItem).Split(' ')[0];
        var certificate = Path.Combine(DesktopSettings.DirectoryPath, "certificates", "device", "server.pfx");
        var candidate = new HostSettings { RepositoryPath = RepositorySetting.Text.Trim(), DataDirectory = DesktopSettings.DirectoryPath, ListenAddress = address, Port = int.Parse(PortSetting.Text, System.Globalization.CultureInfo.InvariantCulture), CertificatePath = certificate, PinnedCertificate = true };
        candidate.Validate(); using var setupLease = new HostLease(candidate); setupLease.Acquire();
        await Task.Run(() => PeerCertificates.Provision(candidate));
        AddressSetting.Text = address; CertificateSetting.Text = certificate; DirectOriginSetting.Text = "";
        settings.Host.PinnedCertificate = true; settings.Host.CertificatePath = certificate;
        SaveConfiguration();
        AppDialogs.Show(this, "Wi-Fi HTTPS saved. Start the server and scan the pairing QR under Devices, or copy its full pairing string. No certificate installation is needed. Allow the chat port through Windows Firewall.", "Wi-Fi setup");
    });
    private static bool IsPrivateAddress(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        return bytes[0] == 10 || bytes[0] == 172 && bytes[1] is >= 16 and <= 31 || bytes[0] == 192 && bytes[1] == 168;
    }
    private static string[] PrivateInterfaceLabels() => NetworkInterface.GetAllNetworkInterfaces()
        .Where(network => network.OperationalStatus == OperationalStatus.Up && network.NetworkInterfaceType != NetworkInterfaceType.Loopback)
        .OrderBy(network => network.GetIPProperties().GatewayAddresses.Any(gateway => gateway.Address.AddressFamily == AddressFamily.InterNetwork && !gateway.Address.Equals(IPAddress.Any)) ? 0 : 1)
        .ThenBy(network => network.NetworkInterfaceType == NetworkInterfaceType.Wireless80211 ? 0 : network.NetworkInterfaceType == NetworkInterfaceType.Ethernet ? 1 : 2)
        .SelectMany(network => network.GetIPProperties().UnicastAddresses.Where(item => item.Address.AddressFamily == AddressFamily.InterNetwork && IsPrivateAddress(item.Address)).Select(item => $"{item.Address} ({network.Name})"))
        .Distinct().ToArray();
    private async void SetupDirectInternet(object sender, RoutedEventArgs e) => await Act(async () =>
    {
        if (transitioning) return;
        if (relay is not null) throw new InvalidOperationException("Stop the server before configuring direct internet. Active work ends when the server stops.");
        var addresses = PrivateInterfaceLabels();
        if (addresses.Length == 0) throw new InvalidOperationException("No private desktop IPv4 address was found. Connect to your home network first.");
        var interfaces = new ComboBox { ItemsSource = addresses, SelectedIndex = 0, Margin = new Thickness(16, 4, 16, 10) };
        var publicIp = new TextBox { Text = settings.Host.DirectOrigin.Length > 0 ? new Uri(settings.Host.DirectOrigin).Host : "", Margin = new Thickness(16, 4, 16, 10) };
        var detect = new Button { Content = "Detect public IP", Margin = new Thickness(16, 0, 16, 6), HorizontalAlignment = HorizontalAlignment.Left };
        var detectionStatus = new TextBlock { Text = "Queries api.ipify.org for your IPv4 address. No chat, credentials or pairing code is sent. You can also enter the address manually.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(16, 0, 16, 10) };
        using var lookupCancellation = new CancellationTokenSource();
        var externalPort = new TextBox { Text = settings.Host.DirectOrigin.Length > 0 ? new Uri(settings.Host.DirectOrigin).Port.ToString(System.Globalization.CultureInfo.InvariantCulture) : PortSetting.Text, Margin = new Thickness(16, 4, 16, 10) };
        var apply = new Button { Content = "Save direct connection", Margin = new Thickness(16) };
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock { Text = "Use your router's public/WAN IPv4 address. No domain, tunnel, relay, or PocketAgent hosting is used. You must configure port forwarding and the firewall yourself.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(16) });
        panel.Children.Add(new TextBlock { Text = "Desktop LAN address (forward traffic here)", Margin = new Thickness(16, 0, 16, 0) }); panel.Children.Add(interfaces);
        panel.Children.Add(new TextBlock { Text = "Choose your physical Wi-Fi/Ethernet adapter, not a VPN or virtual adapter. For the first connection test, disconnect the VPN and use your router's WAN address; a VPN can change both detected IP and reply routing.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(16, 0, 16, 10) });
        panel.Children.Add(new TextBlock { Text = "Router public IPv4 address", Margin = new Thickness(16, 0, 16, 0) }); panel.Children.Add(publicIp); panel.Children.Add(detect); panel.Children.Add(detectionStatus);
        panel.Children.Add(new TextBlock { Text = "External chat TCP port", Margin = new Thickness(16, 0, 16, 0) }); panel.Children.Add(externalPort);
        panel.Children.Add(new TextBlock { Text = "Forward only the chat port to this desktop and allow it through Windows Firewall. After starting, scan the QR under Devices or copy its pairing string. Ports 80/443 and Caddy changes are not needed.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(16) }); panel.Children.Add(apply);
        var dialog = new Window { Title = "Direct internet setup", Owner = this, Content = new ScrollViewer { Content = panel }, Width = 620, MaxHeight = SystemParameters.WorkArea.Height - 60, SizeToContent = SizeToContent.Height, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        dialog.Closed += (_, _) => lookupCancellation.Cancel();
        detect.Click += async (_, _) =>
        {
            var original = publicIp.Text;
            detect.IsEnabled = false; apply.IsEnabled = false; detectionStatus.Text = "Detecting public IPv4…";
            try
            {
                var found = await PublicAddressLookup.Detect(lookupCancellation.Token);
                if (publicIp.Text == original) publicIp.Text = found;
                detectionStatus.Text = $"Detected {found}. Manual edits are preserved. A VPN may report its exit address; detection does not prove router forwarding or public reachability.";
            }
            catch (OperationCanceledException) { if (dialog.IsVisible) detectionStatus.Text = "Detection timed out. Retry or enter the public IPv4 manually."; }
            catch (ApiException exception) { detectionStatus.Text = exception.Message; }
            finally { detect.IsEnabled = true; apply.IsEnabled = true; }
        };
        apply.Click += (_, _) => dialog.DialogResult = true;
        if (dialog.ShowDialog() != true) return;
        var localIp = ((string)interfaces.SelectedItem).Split(' ')[0];
        var origin = $"https://{publicIp.Text.Trim()}:{int.Parse(externalPort.Text, System.Globalization.CultureInfo.InvariantCulture)}";
        var candidate = new HostSettings { RepositoryPath = RepositorySetting.Text.Trim(), DataDirectory = DesktopSettings.DirectoryPath, ListenAddress = localIp, Port = int.Parse(PortSetting.Text, System.Globalization.CultureInfo.InvariantCulture), DirectOrigin = origin, CertificatePath = Path.Combine(DesktopSettings.DirectoryPath, "certificates", "device", "server.pfx"), PinnedCertificate = true };
        candidate.Validate();
        using var setupLease = new HostLease(candidate); setupLease.Acquire();
        await Task.Run(() => PeerCertificates.Provision(candidate));
        AddressSetting.Text = localIp; CertificateSetting.Text = candidate.CertificatePath; DirectOriginSetting.Text = candidate.DirectOrigin;
        settings.Host.PinnedCertificate = true; settings.Host.CertificatePath = candidate.CertificatePath;
        SaveConfiguration();
        AppDialogs.Show(this, $"Direct connection saved: {candidate.ClientOrigin}\n\nForward external TCP {new Uri(candidate.DirectOrigin).Port} to {localIp} TCP {candidate.Port}, and allow the listener through Windows Firewall. Start the server, then use Devices to scan the pairing QR or copy its pairing string. No certificate installation or ports 80/443 are needed.", "Direct internet setup");
    });
    private async void CheckConnectivity(object sender, RoutedEventArgs e) => await Act(async () =>
    {
        if (relay is null) throw new InvalidOperationException("Start the server before running connection diagnostics.");
        var report = await relay.Services.GetRequiredService<ConnectivityDiagnostics>().Check();
        var text = new TextBox { Text = string.Join("\n\n", report.Checks), IsReadOnly = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(16) };
        new Window { Title = report.LocalConnectionVerified ? "Connection diagnostics - local HTTPS verified" : "Connection diagnostics - setup needs attention", Owner = this, Content = text, Width = 760, Height = 580, WindowStartupLocation = WindowStartupLocation.CenterOwner }.ShowDialog();
    });
    private async void StartServer(object sender, RoutedEventArgs e)
    {
        if (relay is not null || transitioning) return;
        transitioning = true; StartButton.IsEnabled = false;
        await Act(async () =>
        {
            SaveConfiguration();
            var candidate = RelayHost.Create([], settings.Host);
            try { await candidate.StartAsync(); }
            catch { await candidate.DisposeAsync(); throw; }
            relay = candidate;
            sessionSignature = "";
            ServerStatus.Text = $"Running · {settings.Host.RepositoryPath}";
            ConnectionAddress.Text = settings.Host.ClientOrigin;
            StopButton.IsEnabled = true; SaveButton.IsEnabled = false;
            PermissionModeSetting.IsEnabled = false;
            await Refresh();
        });
        transitioning = false; StartButton.IsEnabled = relay is null;
    }
    private async Task<bool> ConfirmStop()
    {
        if (relay is null) return true;
        var sessions = await Conversations.List();
        var pending = relay.Services.GetRequiredService<LocalStore>().Scheduled().Count(job => job.Status == "pending");
        return !sessions.Any(session => session.Status is "running" or "awaiting-input" or "starting" or "uncertain") && pending == 0 ||
            AppDialogs.Show(this, $"Stopping ends the Codex process and active work. {pending} pending scheduled message(s) will wait until the server runs again. Stop anyway?", "Stop server", MessageBoxButton.YesNo) == MessageBoxResult.Yes;
    }
    private async Task Shutdown()
    {
        var running = relay; relay = null;
        if (running is not null)
        {
            try { await running.StopAsync(); }
            finally { await running.DisposeAsync(); }
        }
        selected = null; conversation = null; rendered = ""; SessionList.ItemsSource = null;
        ConversationPanel.IsEnabled = false; Transcript.Children.Clear(); Composer.Text = "";
        PairingQr.Source = null; qrRendered = "";
        PairingDisplay.Text = ""; DeviceList.Children.Clear(); ConnectionAddress.Text = "";
        ServerStatus.Text = "Server stopped"; StartButton.IsEnabled = true; StopButton.IsEnabled = false; SaveButton.IsEnabled = true;
        PermissionModeSetting.IsEnabled = true;
    }
    private async void StopServer(object sender, RoutedEventArgs e)
    {
        if (transitioning) return;
        transitioning = true;
        await Act(async () => { if (await ConfirmStop()) await Shutdown(); });
        transitioning = false;
    }
    private async void WindowClosing(object? sender, CancelEventArgs e)
    {
        if (closing) return;
        e.Cancel = true;
        if (transitioning) return;
        await Act(async () =>
        {
            if (!await ConfirmStop()) return;
            timer.Stop(); await Shutdown(); closing = true;
            _ = Dispatcher.BeginInvoke(Close);
        });
    }

    private sealed record SessionEntry(string Id, string Label);
    private async Task Refresh()
    {
        if (relay is null || refreshing) return;
        refreshing = true;
        await Act(async () =>
        {
            var running = relay;
            CertificateStatus.Text = running.Services.GetRequiredService<PeerCertificates>().Status;
            var sessions = await Conversations.List();
            if (relay != running) return;
            var usage = await running.Services.GetRequiredService<ProviderUsage>().Check();
            UsageDisplay.Text = $"5h: {UsageText(usage.FiveHour)} · Weekly: {UsageText(usage.Weekly)}";
            UsageDisplay.ToolTip = $"Five-hour reset: {usage.FiveHour?.ResetsAt?.LocalDateTime.ToString("g") ?? "unavailable"}\nWeekly reset: {usage.Weekly?.ResetsAt?.LocalDateTime.ToString("g") ?? "unavailable"}";
            var signature = JsonSerializer.Serialize(sessions);
            if (selected is not null && !sessions.Any(session => session.Id == selected))
            {
                selected = null; conversation = null; rendered = "";
                ConversationPanel.IsEnabled = false; Transcript.Children.Clear();
                settingDraft = true; Composer.Clear(); settingDraft = false;
            }
            if (signature != sessionSignature)
            {
                changingList = true;
                SessionList.ItemsSource = sessions.Select(session => new SessionEntry(session.Id, $"{session.Title}\n{session.Status}")).ToArray();
                SessionList.SelectedItem = SessionList.Items.Cast<SessionEntry>().FirstOrDefault(item => item.Id == selected);
                changingList = false; sessionSignature = signature;
            }
            var code = running.Services.GetRequiredService<PairingCode>().Current;
            PairingDisplay.Text = code.Consumed ? "Pairing used — generate a new QR" : code.ExpiresAt <= DateTimeOffset.UtcNow ? "Pairing expired — generate a new QR" : settings.Host.UseHttps ? "Scan or copy to pair" : "Set up HTTPS to pair a phone";
            PairingExpiry.Text = $"Single use · expires {code.ExpiresAt.LocalDateTime:t}";
            var pairing = running.Services.GetRequiredService<PairingCode>();
            if (code.Consumed || code.ExpiresAt <= DateTimeOffset.UtcNow) { PairingQr.Source = null; qrRendered = ""; }
            else if (settings.Host.UseHttps)
            {
                var payload = PeerCertificates.PairingPayload(settings.Host, pairing.Ticket);
                if (payload != qrRendered)
                {
                    using var data = QRCoder.QRCodeGenerator.GenerateQrCode(payload, QRCoder.QRCodeGenerator.ECCLevel.M);
                    using var qr = new QRCoder.PngByteQRCode(data);
                    using var stream = new MemoryStream(qr.GetGraphic(6));
                    var bitmap = new System.Windows.Media.Imaging.BitmapImage(); bitmap.BeginInit(); bitmap.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad; bitmap.StreamSource = stream; bitmap.EndInit(); bitmap.Freeze();
                    PairingQr.Source = bitmap; qrRendered = payload;
                }
            }
            if (Navigation.SelectedIndex == 1) RenderDevices();
            if (selected is null) return;
            var expected = selected;
            var snapshot = await Conversations.Get(expected);
            if (relay != running || selected != expected) return;
            conversation = snapshot;
            ConversationPanel.IsEnabled = true;
            ConversationTitle.Text = snapshot.Title;
            ModelButton.Content = snapshot.Model ?? "Model: Codex settings";
            ModelButton.IsEnabled = snapshot.ActiveTurnId is null && snapshot.Status is not ("starting" or "uncertain" or "disconnected") && !sending;
            var policy = snapshot.SandboxMode is null ? $"{CodexPermissions.Label(snapshot.PermissionMode)} selected · provider policy checked on start/resume" :
                $"Provider: {snapshot.SandboxMode} · approvals: {snapshot.ApprovalPolicy ?? "not reported"}";
            var work = snapshot.ActiveTurnStartedAt is { } start ? $" · {Elapsed((long)(DateTimeOffset.UtcNow - start).TotalMilliseconds)}" :
                snapshot.LastWorkDurationMs is { } duration ? $" · worked {Elapsed(duration)}" : "";
            ConversationMeta.Text = $"{snapshot.Status}{work} · {snapshot.HostName}\n{snapshot.WorkingDirectory}\n{policy}";
            InterruptButton.IsEnabled = snapshot.ActiveTurnId is not null;
            SendButton.Content = snapshot.ActiveTurnId is null ? "Send" : "Steer";
            SendButton.IsEnabled = !sending && snapshot.Status is not ("starting" or "uncertain" or "disconnected");
            var content = JsonSerializer.Serialize(new { snapshot.Items, snapshot.Requests, snapshot.Error });
            if (content != rendered) { RenderTranscript(snapshot); rendered = content; }
        }, clearError: false);
        refreshing = false;
    }
    private void RenderDevices()
    {
        DeviceList.Children.Clear();
        var store = relay!.Services.GetRequiredService<LocalStore>();
        foreach (var device in store.Devices())
        {
            var row = new DockPanel { Margin = new Thickness(0, 5, 0, 5) };
            var revoke = new Button { Content = "Revoke" }; DockPanel.SetDock(revoke, Dock.Right);
            revoke.Click += (_, _) => { store.Revoke(device.Id); RenderDevices(); };
            row.Children.Add(revoke); row.Children.Add(new TextBlock { Text = $"{device.Name}\nExpires {device.ExpiresAt.LocalDateTime:d}", VerticalAlignment = VerticalAlignment.Center });
            DeviceList.Children.Add(row);
        }
    }
    private async void SessionSelected(object sender, SelectionChangedEventArgs e)
    {
        if (changingList || SessionList.SelectedItem is not SessionEntry entry) return;
        selected = entry.Id; conversation = null; rendered = "";
        settingDraft = true; Composer.Text = drafts.GetValueOrDefault(selected, ""); settingDraft = false;
        await Refresh();
    }
    private async void NewConversation(object sender, RoutedEventArgs e) => await Act(async () =>
    {
        if (relay is null) throw new InvalidOperationException("Start the server first.");
        var directory = new TextBox { Text = conversation?.WorkingDirectory ?? settings.Host.RepositoryPath, Margin = new Thickness(16, 4, 16, 8) };
        var models = await relay.Services.GetRequiredService<ProviderDiagnostics>().Models();
        var choices = new ComboBox { Margin = new Thickness(16, 4, 16, 8), DisplayMemberPath = "DisplayName", ItemsSource = new[] { new ModelChoice(null, "Use Codex settings") }.Concat(models.Select(model => new ModelChoice(model.Model, model.DisplayName))).ToArray(), SelectedIndex = 0 };
        var browse = new Button { Content = "Browse folders", Margin = new Thickness(16, 0, 16, 8) };
        browse.Click += (_, _) => { var picker = new OpenFolderDialog { InitialDirectory = directory.Text }; if (picker.ShowDialog(this) == true) directory.Text = picker.FolderName; };
        var create = new Button { Content = "Create conversation", Margin = new Thickness(16) };
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock { Text = "Working folder on this machine", Margin = new Thickness(16, 16, 16, 0) });
        panel.Children.Add(directory); panel.Children.Add(browse);
        panel.Children.Add(new TextBlock { Text = "Model", Margin = new Thickness(16, 8, 16, 0) }); panel.Children.Add(choices); panel.Children.Add(create);
        var dialog = new Window { Title = "New conversation", Owner = this, Content = panel, Width = 560, SizeToContent = SizeToContent.Height, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        create.Click += (_, _) => dialog.DialogResult = true;
        if (dialog.ShowDialog() != true) return;
        var created = await Conversations.Create("", directory.Text, ((ModelChoice)choices.SelectedItem).Model); selected = created.Id; conversation = created; rendered = "";
        settingDraft = true; Composer.Text = ""; settingDraft = false;
        await Refresh(); Composer.Focus();
    });
    private sealed record ModelChoice(string? Model, string DisplayName);
    private async void ChangeModel(object sender, RoutedEventArgs e) => await Act(async () =>
    {
        if (relay is null || conversation is null) return;
        var id = conversation.Id;
        var models = await relay.Services.GetRequiredService<ProviderDiagnostics>().Models();
        var choices = new ComboBox { Margin = new Thickness(16), DisplayMemberPath = "DisplayName", ItemsSource = models, SelectedIndex = Array.FindIndex(models, model => model.Model == conversation.Model) };
        var save = new Button { Content = "Use selected model", Margin = new Thickness(16) };
        var panel = new StackPanel(); panel.Children.Add(new TextBlock { Text = "Applies to future messages, including scheduled messages.", Margin = new Thickness(16) }); panel.Children.Add(choices); panel.Children.Add(save);
        var dialog = new Window { Title = "Conversation model", Owner = this, Content = panel, Width = 500, SizeToContent = SizeToContent.Height, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        save.Click += (_, _) => { if (choices.SelectedItem is not null) dialog.DialogResult = true; };
        if (dialog.ShowDialog() == true) { await Conversations.SetModel(id, ((ModelOption)choices.SelectedItem).Model); await Refresh(); }
    });
    private async void ArchiveConversation(object sender, RoutedEventArgs e) => await Act(async () =>
    {
        if (relay is null || conversation is null) return;
        var id = conversation.Id;
        if (AppDialogs.Show(this, "Archive this conversation and cancel its pending scheduled messages? You can restore it from Archived.", "Archive conversation", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
        await Conversations.Archive(id, true); await Refresh();
    });
    private async void ShowArchived(object sender, RoutedEventArgs e) => await Act(async () =>
    {
        if (relay is null) throw new InvalidOperationException("Start the server first.");
        var archived = await Conversations.List(true);
        var list = new ListBox { ItemsSource = archived.Select(item => new SessionEntry(item.Id, $"{item.Title}\n{item.WorkingDirectory}")), DisplayMemberPath = "Label", Margin = new Thickness(16), Height = 280 };
        var restore = new Button { Content = "Restore selected conversation", Margin = new Thickness(16) };
        var panel = new StackPanel(); panel.Children.Add(new TextBlock { Text = archived.Length == 0 ? "No archived conversations." : "Restoring does not requeue canceled messages.", Margin = new Thickness(16) }); panel.Children.Add(list); panel.Children.Add(restore);
        var dialog = new Window { Title = "Archived conversations", Owner = this, Content = panel, Width = 560, SizeToContent = SizeToContent.Height, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        restore.Click += (_, _) => { if (list.SelectedItem is not null) dialog.DialogResult = true; };
        if (dialog.ShowDialog() == true) { await Conversations.Archive(((SessionEntry)list.SelectedItem).Id, false); await Refresh(); }
    });
    private void DraftChanged(object sender, TextChangedEventArgs e)
    {
        if (!settingDraft && selected is not null) drafts[selected] = Composer.Text;
    }
    private async void SendMessage(object sender, RoutedEventArgs e) => await Submit();
    private async void ComposerKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) { e.Handled = true; await Submit(); }
    }
    private async Task Submit()
    {
        if (sending || relay is null || conversation is null || string.IsNullOrWhiteSpace(Composer.Text)) return;
        var id = conversation.Id; var expectedTurn = conversation.ActiveTurnId; var text = Composer.Text;
        sending = true; SendButton.IsEnabled = false;
        await Act(async () =>
        {
            var receipt = await relay.Services.GetRequiredService<ScheduledMessages>().Send(id, text, expectedTurn, null);
            if (receipt.Queued) ErrorDisplay.Text = receipt.Message;
            if (drafts.GetValueOrDefault(id) == text) drafts[id] = "";
            if (selected == id && Composer.Text == text) Composer.Clear();
        });
        sending = false; await Refresh();
    }
    private async void InterruptWork(object sender, RoutedEventArgs e) => await Act(async () =>
    {
        if (relay is not null && selected is not null) await Conversations.Interrupt(selected);
        await Refresh();
    });
    private async void RenameConversation(object sender, RoutedEventArgs e)
    {
        if (conversation is null) return;
        var id = conversation.Id;
        var input = new TextBox { Text = conversation.Title, MaxLength = 100, Margin = new Thickness(16) };
        var save = new Button { Content = "Save name", Margin = new Thickness(16) };
        var panel = new StackPanel(); panel.Children.Add(input); panel.Children.Add(save);
        var dialog = new Window { Title = "Rename conversation", Owner = this, Content = panel, Width = 440, SizeToContent = SizeToContent.Height, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        save.Click += (_, _) => dialog.DialogResult = true;
        if (dialog.ShowDialog() == true) await Act(async () => { await Conversations.Rename(id, input.Text); await Refresh(); });
    }

    private void RenderTranscript(ConversationSnapshot snapshot)
    {
        var atBottom = TranscriptScroll.ScrollableHeight - TranscriptScroll.VerticalOffset < 60;
        Transcript.Children.Clear();
        if (snapshot.Items.Length == 0) Transcript.Children.Add(new TextBlock { Text = "Send a message to start working.", Margin = new Thickness(12) });
        var start = snapshot.Items.Length;
        var characters = 0;
        while (start > 0 && snapshot.Items[start - 1].Text.Length + snapshot.Items[start - 1].Detail.Length <= MaxRenderedCharacters - characters && snapshot.Items.Length - start < MaxRenderedItems)
        {
            characters += snapshot.Items[start - 1].Text.Length + snapshot.Items[start - 1].Detail.Length;
            start--;
        }
        if (start > 0)
            Transcript.Children.Add(new TextBlock { Text = "Earlier messages are hidden in this view to keep the desktop responsive. They remain in the conversation history.", Foreground = Brushes.DarkSeaGreen, Margin = new Thickness(12, 8, 12, 12) });

        string? lastMinute = null;
        foreach (var item in snapshot.Items.Skip(start))
        {
            if (item.Type is "userMessage" or "agentMessage" && item.Timestamp is { } timestamp)
            {
                var minute = timestamp.LocalDateTime.ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture);
                if (minute != lastMinute) Transcript.Children.Add(new TextBlock { Text = timestamp.LocalDateTime.ToString("t"), FontSize = 11, Foreground = Brushes.DarkSeaGreen, Margin = new Thickness(8, 5, 0, 5) });
                lastMinute = minute;
            }
            var content = new TextBlock { Text = item.Type is "userMessage" or "agentMessage" ? item.Text : $"{item.Type} · {item.Status}\n{item.Detail}", Margin = new Thickness(12), TextWrapping = TextWrapping.Wrap };
            if (item.Type == "agentMessage") { content.Margin = new Thickness(8, 8, 8, 18); Transcript.Children.Add(content); }
            else Transcript.Children.Add(new Border { Child = content, Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(item.Type == "userMessage" ? "#2A3B2D" : "#19271F")), CornerRadius = new CornerRadius(8), HorizontalAlignment = item.Type == "userMessage" ? HorizontalAlignment.Right : HorizontalAlignment.Stretch, MaxWidth = item.Type == "userMessage" ? 760 : double.PositiveInfinity, Margin = new Thickness(0, 0, 0, 10) });
        }
        if (snapshot.Error is not null) Transcript.Children.Add(new TextBlock { Text = snapshot.Error, Foreground = Brushes.Salmon, Margin = new Thickness(12) });
        foreach (var request in snapshot.Requests) RenderRequest(snapshot.Id, request);
        if (atBottom) TranscriptScroll.ScrollToEnd();
    }
    private void RenderRequest(string id, PendingRequest request)
    {
        var panel = new StackPanel { Margin = new Thickness(12) };
        panel.Children.Add(new TextBlock { Text = request.Supported ? "Your input is needed" : "Unsupported request · stop work and handle this on the desktop", FontWeight = FontWeights.Bold });
        if (request.Parameters.Text("command").Length > 0) panel.Children.Add(new TextBlock { Text = request.Parameters.Text("command") });
        if (request.Parameters.Text("reason").Length > 0) panel.Children.Add(new TextBlock { Text = request.Parameters.Text("reason") });
        if (request.Supported && request.Method == "item/tool/requestUserInput")
        {
            var answers = new Dictionary<string, Func<string>>();
            foreach (var question in request.Parameters.GetProperty("questions").EnumerateArray())
            {
                panel.Children.Add(new TextBlock { Text = question.Text("question"), Margin = new Thickness(0, 10, 0, 0) });
                if (question.Field("isSecret").ValueKind == JsonValueKind.True)
                { var input = new PasswordBox { Margin = new Thickness(0, 5, 0, 10) }; panel.Children.Add(input); answers[question.Text("id")] = () => input.Password; }
                else
                {
                    var input = new TextBox(); panel.Children.Add(input); answers[question.Text("id")] = () => input.Text;
                    if (question.Field("options").ValueKind == JsonValueKind.Array)
                        foreach (var option in question.GetProperty("options").EnumerateArray())
                        { var value = option.Text("label"); var button = new Button { Content = value, ToolTip = option.Text("description"), HorizontalAlignment = HorizontalAlignment.Left }; button.Click += (_, _) => input.Text = value; panel.Children.Add(button); }
                }
            }
            var submit = new Button { Content = "Send answers" };
            submit.Click += async (_, _) => await Act(async () => { await Conversations.Answer(id, request.Id, new(null, answers.ToDictionary(pair => pair.Key, pair => new[] { pair.Value() }))); await Refresh(); });
            panel.Children.Add(submit);
        }
        else if (request.Supported)
            foreach (var decision in request.Decisions)
            {
                var button = new Button { Content = decision == "accept" ? "Approve once" : decision == "decline" ? "Deny" : "Cancel work", HorizontalAlignment = HorizontalAlignment.Left };
                button.Click += async (_, _) => await Act(async () => { await Conversations.Answer(id, request.Id, new(decision, null)); await Refresh(); });
                panel.Children.Add(button);
            }
        Transcript.Children.Add(new Border { Child = panel, BorderBrush = Brushes.DarkKhaki, BorderThickness = new Thickness(1), Margin = new Thickness(0, 5, 0, 10) });
    }

    private void RenewPairing(object sender, RoutedEventArgs e) { relay?.Services.GetRequiredService<PairingCode>().Renew(); _ = Refresh(); }
    private void CopyPairing(object sender, RoutedEventArgs e)
    {
        if (relay is null || qrRendered.Length == 0) { ErrorDisplay.Text = "Start HTTPS and generate a fresh pairing QR first."; return; }
        var code = relay.Services.GetRequiredService<PairingCode>().Current;
        if (code.Consumed || code.ExpiresAt <= DateTimeOffset.UtcNow) { ErrorDisplay.Text = "Generate a new pairing QR."; return; }
        Clipboard.SetText(qrRendered);
        ErrorDisplay.Text = "Pairing string copied. Treat it like a temporary password; paste it only into your phone app.";
    }
    private void CopyAddress(object sender, RoutedEventArgs e) { if (ConnectionAddress.Text.Length > 0) Clipboard.SetText(ConnectionAddress.Text); }
    private void LocateAndroid(object sender, RoutedEventArgs e)
    {
        var apk = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "android", $"PocketAgent-{BuildInfo.Version}-android.apk"));
        if (File.Exists(apk)) Process.Start(new ProcessStartInfo("explorer.exe") { Arguments = $"/select,\"{apk}\"", UseShellExecute = true });
        else ErrorDisplay.Text = "The signed Android APK is in the artifacts folder after scripts/Build-Android.ps1 runs.";
    }
    private async void CheckProvider(object sender, RoutedEventArgs e) => await Act(async () =>
    {
        if (relay is null) throw new InvalidOperationException("Start the server first.");
        var status = await relay.Services.GetRequiredService<ProviderDiagnostics>().Check();
        var binary = relay.Services.GetRequiredService<CodexConnection>().Executable;
        ProviderDisplay.Text = $"{(status.SignInAvailable ? "Desktop sign-in found" : "Desktop sign-in missing")}\nModel: {status.ConfiguredModel ?? status.DefaultModel ?? "provider default"}\n{(status.ConfiguredModelInCatalog == false ? "Configured model is absent from this executable's catalog. " : "")}Inference has not been verified.\n{binary?.Source}\n{binary?.Path}";
    });
    private void OpenGuide(object sender, RoutedEventArgs e)
    {
        var file = Path.Combine(AppContext.BaseDirectory, "documentation", "SETUP.md");
        if (File.Exists(file)) Process.Start(new ProcessStartInfo(file) { UseShellExecute = true });
        else ErrorDisplay.Text = "See documentation/SETUP.md in the repository or release package.";
    }
    private static string UsageText(UsageWindow? window) => window is null ? "unavailable" : $"{window.RemainingPercent}%";
    private static string Elapsed(long milliseconds)
    {
        var time = TimeSpan.FromMilliseconds(Math.Max(0, milliseconds));
        return time.TotalHours >= 1 ? $"{(int)time.TotalHours}h {time.Minutes}m" : time.TotalMinutes >= 1 ? $"{(int)time.TotalMinutes}m {time.Seconds}s" : $"{time.Seconds}s";
    }
    private async void ScheduleMessage(object sender, RoutedEventArgs e)
    {
        if (relay is null || conversation is null || string.IsNullOrWhiteSpace(Composer.Text)) { ErrorDisplay.Text = "Write a message, then choose Schedule."; return; }
        var id = conversation.Id; var text = Composer.Text;
        var date = new DatePicker { SelectedDate = DateTime.Today, Margin = new Thickness(16) };
        var time = new TextBox { Text = DateTime.Now.AddMinutes(5).ToString("HH:mm"), Margin = new Thickness(16) };
        var save = new Button { Content = "Schedule message", Margin = new Thickness(16) };
        var panel = new StackPanel(); panel.Children.Add(new TextBlock { Text = "Desktop local date and time (HH:mm)", Margin = new Thickness(16) }); panel.Children.Add(date); panel.Children.Add(time); panel.Children.Add(save);
        var dialog = new Window { Title = "Schedule message", Owner = this, Content = panel, Width = 420, SizeToContent = SizeToContent.Height, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        save.Click += (_, _) => dialog.DialogResult = true;
        if (dialog.ShowDialog() != true) return;
        await Act(async () =>
        {
            if (date.SelectedDate is null || !TimeSpan.TryParseExact(time.Text, @"hh\:mm", System.Globalization.CultureInfo.InvariantCulture, out var clock) || clock.TotalHours >= 24)
                throw new InvalidOperationException("Choose a date and time in HH:mm format.");
            var local = DateTime.SpecifyKind(date.SelectedDate.Value.Date + clock, DateTimeKind.Local);
            if (TimeZoneInfo.Local.IsInvalidTime(local) || TimeZoneInfo.Local.IsAmbiguousTime(local) || local <= DateTime.Now) throw new InvalidOperationException("Choose an unambiguous future local time.");
            await relay.Services.GetRequiredService<ScheduledMessages>().Schedule(id, text, new DateTimeOffset(local), null);
            if (drafts.GetValueOrDefault(id) == text) drafts[id] = "";
            if (selected == id && Composer.Text == text) Composer.Clear();
            ErrorDisplay.Text = "Message scheduled. The desktop must be running at that time.";
        });
    }
    private void ShowScheduled(object sender, RoutedEventArgs e)
    {
        if (relay is null || selected is null) return;
        var messages = relay.Services.GetRequiredService<ScheduledMessages>();
        var panel = new StackPanel { Margin = new Thickness(16) };
        foreach (var job in relay.Services.GetRequiredService<LocalStore>().Scheduled(selected))
        {
            var row = new StackPanel { Margin = new Thickness(0, 0, 0, 16) };
            row.Children.Add(new TextBlock { Text = $"{job.Status} · {job.DueAt.LocalDateTime:g}\n{job.Text}\n{job.Error}", TextWrapping = TextWrapping.Wrap });
            if (job.Status == "pending")
            {
                var cancel = new Button { Content = "Cancel scheduled message", HorizontalAlignment = HorizontalAlignment.Left };
                cancel.Click += async (_, _) => await Act(async () => { await messages.Cancel(job.Id); cancel.IsEnabled = false; cancel.Content = "Canceled"; });
                row.Children.Add(cancel);
            }
            panel.Children.Add(row);
        }
        if (panel.Children.Count == 0) panel.Children.Add(new TextBlock { Text = "No scheduled messages in this conversation." });
        new Window { Title = "Scheduled messages", Owner = this, Content = new ScrollViewer { Content = panel }, Width = 580, Height = 480, WindowStartupLocation = WindowStartupLocation.CenterOwner }.Show();
    }
}
