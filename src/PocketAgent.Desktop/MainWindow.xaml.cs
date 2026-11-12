using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
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
    private readonly DispatcherTimer updateTimer = new() { Interval = TimeSpan.FromHours(6) };
    private readonly Dictionary<string, string> drafts = [];
    private readonly Dictionary<string, List<DisplayItem>> optimisticItems = [];
    private readonly Dictionary<string, List<DisplayItem>> olderHistory = [];
    private readonly Dictionary<string, string?> historyCursors = [];
    private readonly HashSet<string> expandedWorkItems = [];
    private ConversationSnapshot? conversation;
    private string? selected;
    private string rendered = "";
    private string sessionSignature = "";
    private string qrRendered = "";
    private bool refreshing, sending, settingDraft, changingList, closing, transitioning, loadingHistory;
    private DesktopUpdate? pendingUpdate;
    private string? updateHealthFile;
    private string? expectedUpdateVersion;
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
        var healthArgument = Array.IndexOf(arguments, "--health-file");
        if (healthArgument >= 0 && healthArgument + 1 < arguments.Length) updateHealthFile = arguments[healthArgument + 1];
        var updateVersionArgument = Array.IndexOf(arguments, "--update-version");
        if (updateVersionArgument >= 0 && updateVersionArgument + 1 < arguments.Length) expectedUpdateVersion = arguments[updateVersionArgument + 1];
        var repositoryArgument = Array.IndexOf(arguments, "--repository");
        if (RepositorySetting.Text.Length == 0 && repositoryArgument >= 0 && repositoryArgument + 1 < arguments.Length)
            RepositorySetting.Text = arguments[repositoryArgument + 1];
        CodexSetting.Text = settings.Host.CodexExecutable;
        PermissionModeSetting.SelectedValue = settings.Host.PermissionMode;
        StartServerOnLaunchSetting.IsChecked = settings.StartServerOnLaunch;
        LaunchOnWindowsStartupSetting.IsChecked = settings.LaunchOnWindowsStartup;
        CheckUpdatesOnStartupSetting.IsChecked = settings.CheckUpdatesOnStartup;
        CheckUpdatesPeriodicallySetting.IsChecked = settings.CheckUpdatesPeriodically;
        AutomaticUpdatesSetting.IsChecked = settings.AutomaticUpdates;
        AddressSetting.Text = settings.Host.ListenAddress;
        PortSetting.Text = settings.Host.Port.ToString(System.Globalization.CultureInfo.InvariantCulture);
        CertificateSetting.Text = settings.Host.CertificatePath;
        DirectOriginSetting.Text = settings.Host.DirectOrigin;
        if (settings.Host.RepositoryPath.Length == 0) Navigation.SelectedIndex = 2;
        ResolveCodex();
        timer.Tick += async (_, _) => { UpdateElapsedDisplay(); await Refresh(); };
        timer.Start();
        updateTimer.Tick += async (_, _) => await CheckForUpdatesCore(false);
        updateTimer.Start();
        Loaded += async (_, _) => await WindowReady();
    }

    private void ResolveCodex()
    {
        try { var executable = CodexExecutableResolver.Resolve(settings.Host); CodexResolved.Text = $"{executable.Source}\n{executable.Path}"; }
        catch (Exception exception) { CodexResolved.Text = exception.Message; }
    }

    private async Task WindowReady()
    {
        if (Environment.GetCommandLineArgs().Contains("--background", StringComparer.OrdinalIgnoreCase)) WindowState = WindowState.Minimized;
        WriteUpdateHealth();
        try { StartupRegistration.SetEnabled(settings.LaunchOnWindowsStartup); }
        catch (Exception exception) { UpdateStatus.Text = $"Windows startup registration unavailable: {exception.Message}"; }
        if (settings.StartServerOnLaunch && settings.Host.RepositoryPath.Length > 0)
            await StartServerCore();
        if (settings.CheckUpdatesOnStartup)
            await CheckForUpdatesCore(true);
    }

    private void WriteUpdateHealth()
    {
        if (string.IsNullOrWhiteSpace(updateHealthFile)) return;
        if (!string.IsNullOrWhiteSpace(expectedUpdateVersion) && !string.Equals(expectedUpdateVersion, BuildInfo.Version, StringComparison.OrdinalIgnoreCase)) return;
        try { File.WriteAllText(updateHealthFile, BuildInfo.Version + Environment.NewLine + Environment.ProcessId); } catch { }
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
            StartServerOnLaunch = StartServerOnLaunchSetting.IsChecked == true,
            LaunchOnWindowsStartup = LaunchOnWindowsStartupSetting.IsChecked == true,
            CheckUpdatesOnStartup = CheckUpdatesOnStartupSetting.IsChecked == true,
            CheckUpdatesPeriodically = CheckUpdatesPeriodicallySetting.IsChecked == true,
            AutomaticUpdates = AutomaticUpdatesSetting.IsChecked == true,
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
        candidate.Save(); settings = candidate;
        StartupRegistration.SetEnabled(candidate.LaunchOnWindowsStartup);
        updateTimer.IsEnabled = candidate.CheckUpdatesPeriodically;
        ResolveCodex();
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
    private async void StartServer(object sender, RoutedEventArgs e) => await StartServerCore();
    private async Task StartServerCore()
    {
        if (relay is not null || transitioning) return;
        transitioning = true; StartButton.IsEnabled = false;
        await Act(async () =>
        {
            SaveConfiguration();
            if (IPAddress.TryParse(settings.Host.ListenAddress, out var listenAddress) && !IPAddress.IsLoopback(listenAddress) && !WindowsFirewall.IsConfigured(settings.Host.Port))
            {
                var choice = AppDialogs.Show(this, $"Allow inbound TCP {settings.Host.Port} through Windows Firewall? PocketAgent will create one port-based rule named '{WindowsFirewall.RuleName(settings.Host.Port)}'. It is tied to the port, not this version's executable path, so future updates will not prompt again. The rule is needed for phone connections and can be removed from Windows Firewall later.", "Windows Firewall", MessageBoxButton.YesNo);
                if (choice == MessageBoxResult.Yes && !await Task.Run(() => WindowsFirewall.TryConfigure(settings.Host.Port)))
                    throw new InvalidOperationException("Windows Firewall rule was not created. The server was not started.");
            }
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

    private async void CheckForUpdates(object sender, RoutedEventArgs e) => await CheckForUpdatesCore(false, true);

    private void ViewReleaseNotes(object sender, RoutedEventArgs e)
    {
        if (pendingUpdate is null) return;
        var notes = string.IsNullOrWhiteSpace(pendingUpdate.Notes) ? "No release notes were published for this version." : pendingUpdate.Notes;
        AppDialogs.Show(this, notes, $"PocketAgent {pendingUpdate.Version} release notes");
    }

    private async Task CheckForUpdatesCore(bool startup, bool userRequested = false)
    {
        if (startup && !settings.CheckUpdatesOnStartup) return;
        if (!startup && !userRequested && !settings.CheckUpdatesPeriodically) return;
        try
        {
            var update = await UpdateService.CheckAsync();
            pendingUpdate = update;
            UpdateNotesButton.Visibility = update is null ? Visibility.Collapsed : Visibility.Visible;
            UpdateStatus.Text = update is null ? $"You are running the latest available desktop version ({BuildInfo.Version})." : $"Desktop update {update.Version} is available.";
            if (update is not null && settings.AutomaticUpdates)
                await InstallUpdateCore(false);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or JsonException)
        {
            UpdateNotesButton.Visibility = Visibility.Collapsed;
            UpdateStatus.Text = $"Update check unavailable: {exception.Message}";
        }
    }

    private async void InstallUpdate(object sender, RoutedEventArgs e) => await InstallUpdateCore(true);

    private async Task InstallUpdateCore(bool userRequested)
    {
        if (pendingUpdate is null)
        {
            UpdateStatus.Text = "Check for updates before installing.";
            return;
        }
        if (pendingUpdate.Sha256 is null)
        {
            UpdateStatus.Text = "This release does not publish a checksum, so PocketAgent will not install it automatically.";
            return;
        }
        if (relay is not null)
        {
            var sessions = await Conversations.List();
            if (sessions.Any(session => session.Status is "running" or "awaiting-input" or "starting" or "uncertain"))
            {
                UpdateStatus.Text = "Update waiting for all conversations to become idle.";
                if (userRequested) AppDialogs.Show(this, UpdateStatus.Text, "Update waiting");
                return;
            }
        }
        var updater = UpdateService.Launch(pendingUpdate, Environment.ProcessId);
        if (updater is null)
        {
            UpdateStatus.Text = "The updater is not included in this development build. Install the packaged desktop release to update automatically.";
            return;
        }
        closing = true;
        timer.Stop(); updateTimer.Stop();
        await Shutdown();
        Close();
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
        ConversationPanel.IsEnabled = false; ConversationTitle.Text = "Select a conversation"; ConversationLocation.Text = ""; Transcript.Children.Clear(); Composer.Text = ""; ScheduledSummaryButton.Visibility = Visibility.Collapsed;
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
                ConversationPanel.IsEnabled = false; ConversationTitle.Text = "Select a conversation"; ConversationLocation.Text = ""; Transcript.Children.Clear(); ScheduledSummaryButton.Visibility = Visibility.Collapsed;
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
            if (!historyCursors.ContainsKey(expected)) historyCursors[expected] = snapshot.HistoryBefore;
            var historical = olderHistory.GetValueOrDefault(expected);
            if (historical is { Count: > 0 })
            {
                var currentIds = snapshot.Items.Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
                snapshot = snapshot with { Items = historical.Where(item => currentIds.Add(item.Id)).Concat(snapshot.Items).ToArray() };
            }
            snapshot = snapshot with { HistoryBefore = historyCursors[expected], HistoryTruncated = historyCursors[expected] is not null };
            snapshot = MergeOptimistic(snapshot);
            conversation = snapshot;
            ConversationPanel.IsEnabled = true;
            ConversationTitle.Text = snapshot.Title;
            ConversationLocation.Text = $"{snapshot.HostName} · {snapshot.WorkingDirectory}";
            ModelButton.Content = snapshot.Model is null ? "Model: Codex settings" : $"Model: {snapshot.Model}\nReasoning: {snapshot.ReasoningEffort ?? "model default"}";
            ModelButton.IsEnabled = snapshot.ActiveTurnId is null && snapshot.Status is not ("starting" or "uncertain" or "disconnected") && !sending;
            var policy = snapshot.SandboxMode is null ? $"{CodexPermissions.Label(snapshot.PermissionMode)} selected · provider policy checked on start/resume" :
                $"Provider: {snapshot.SandboxMode} · approvals: {snapshot.ApprovalPolicy ?? "not reported"}";
            var work = snapshot.ActiveTurnStartedAt is { } start ? $" · {Elapsed((long)(DateTimeOffset.UtcNow - start).TotalMilliseconds)}" :
                snapshot.LastWorkDurationMs is { } duration ? $" · worked {Elapsed(duration)}" : "";
            ConversationMeta.Text = $"{snapshot.Status}{work} · {policy}" + (snapshot.HistoryTruncated ? " · older history is hidden" : "");
            ConversationMetaSurface.BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(snapshot.ActiveTurnId is null ? "#47604B" : "#A7BAA7"));
            InterruptButton.IsEnabled = snapshot.ActiveTurnId is not null;
            SendButton.Content = snapshot.ActiveTurnId is null ? "Send" : "Steer";
            SendButton.IsEnabled = !sending && snapshot.Status is not ("starting" or "uncertain" or "disconnected");
            ScheduleButton.IsEnabled = SendButton.IsEnabled && !string.IsNullOrWhiteSpace(Composer.Text);
            UpdateScheduledSummary(snapshot.Id);
            var content = JsonSerializer.Serialize(new { snapshot.Items, snapshot.Requests, snapshot.Error });
            if (content != rendered) { RenderTranscript(snapshot); rendered = content; }
        }, clearError: false);
        refreshing = false;
    }
    private ConversationSnapshot MergeOptimistic(ConversationSnapshot snapshot)
    {
        if (!optimisticItems.TryGetValue(snapshot.Id, out var pending) || pending.Count == 0) return snapshot;
        var canonical = snapshot.Items.ToList();
        var matched = new HashSet<int>();
        foreach (var item in pending.ToArray())
        {
            int? match = null;
            for (var index = 0; index < canonical.Count; index++)
            {
                if (matched.Contains(index)) continue;
                var existing = canonical[index];
                if ((item.ClientMessageId is not null && existing.ClientMessageId == item.ClientMessageId) ||
                    (existing.Type == "userMessage" && existing.Text == item.Text && existing.Timestamp is { } existingTime && item.Timestamp is { } itemTime && Math.Abs((existingTime - itemTime).TotalMinutes) < 10))
                {
                    match = index;
                    break;
                }
            }
            if (match is { } matchedIndex && matched.Add(matchedIndex))
                pending.Remove(item);
            else canonical.Add(item);
        }
        if (pending.Count == 0) optimisticItems.Remove(snapshot.Id);
        return snapshot with { Items = canonical.ToArray() };
    }
    private void UpdateElapsedDisplay()
    {
        if (conversation is null) return;
        var work = conversation.ActiveTurnStartedAt is { } start ? $" · {Elapsed((long)(DateTimeOffset.UtcNow - start).TotalMilliseconds)}" : conversation.LastWorkDurationMs is { } duration ? $" · worked {Elapsed(duration)}" : "";
        var policy = conversation.SandboxMode is null ? $"{CodexPermissions.Label(conversation.PermissionMode)} selected · provider policy checked on start/resume" : $"Provider: {conversation.SandboxMode} · approvals: {conversation.ApprovalPolicy ?? "not reported"}";
        ConversationMeta.Text = $"{conversation.Status}{work} · {policy}" + (conversation.HistoryTruncated ? " · older history is hidden" : "");
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
        selected = entry.Id; conversation = null; rendered = ""; historyCursors.Remove(selected); olderHistory.Remove(selected);
        settingDraft = true; Composer.Text = drafts.GetValueOrDefault(selected, ""); settingDraft = false;
        await Refresh();
    }

    private async void TranscriptScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (loadingHistory || selected is null || conversation?.HistoryBefore is null || TranscriptScroll.ScrollableHeight <= 0 || e.VerticalChange >= 0 || TranscriptScroll.VerticalOffset > 24) return;
        loadingHistory = true;
        var previousExtent = TranscriptScroll.ExtentHeight;
        var previousOffset = TranscriptScroll.VerticalOffset;
        try
        {
            var page = await Conversations.History(selected, conversation.HistoryBefore, 50);
            if (!olderHistory.TryGetValue(selected, out var items)) olderHistory[selected] = items = [];
            var known = items.Select(item => item.Id).Concat(conversation.Items.Select(item => item.Id)).ToHashSet(StringComparer.Ordinal);
            items.InsertRange(0, page.Items.Where(item => known.Add(item.Id)));
            historyCursors[selected] = page.Before;
            await Refresh();
            _ = Dispatcher.BeginInvoke(() => TranscriptScroll.ScrollToVerticalOffset(previousOffset + (TranscriptScroll.ExtentHeight - previousExtent)), DispatcherPriority.Loaded);
        }
        finally { loadingHistory = false; }
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
        var effort = new ComboBox { Margin = new Thickness(16) };
        void RefreshEffortChoices()
        {
            var selected = choices.SelectedItem as ModelOption;
            var options = new[] { new EffortChoice(null, "Use model default") }.Concat((selected?.SupportedReasoningEfforts ?? []).Select(value => new EffortChoice(value, value))).ToArray();
            effort.ItemsSource = options;
            effort.DisplayMemberPath = "Label";
            effort.SelectedIndex = Array.FindIndex(options, option => string.Equals(option.Value, conversation.ReasoningEffort, StringComparison.OrdinalIgnoreCase));
            if (effort.SelectedIndex < 0) effort.SelectedIndex = 0;
        }
        choices.SelectionChanged += (_, _) => RefreshEffortChoices();
        RefreshEffortChoices();
        var save = new Button { Content = "Use selected model", Margin = new Thickness(16) };
        var panel = new StackPanel(); panel.Children.Add(new TextBlock { Text = "Applies to future messages, including scheduled messages.", Margin = new Thickness(16) }); panel.Children.Add(new TextBlock { Text = "Model", Margin = new Thickness(16, 0, 16, 0) }); panel.Children.Add(choices); panel.Children.Add(new TextBlock { Text = "Reasoning effort", Margin = new Thickness(16, 0, 16, 0) }); panel.Children.Add(effort); panel.Children.Add(save);
        var dialog = new Window { Title = "Conversation model", Owner = this, Content = panel, Width = 500, SizeToContent = SizeToContent.Height, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        save.Click += (_, _) => { if (choices.SelectedItem is not null) dialog.DialogResult = true; };
        if (dialog.ShowDialog() == true)
        {
            var selected = (ModelOption)choices.SelectedItem;
            await Conversations.SetModel(id, selected.Model);
            await Conversations.SetReasoning(id, ((EffortChoice)effort.SelectedItem).Value);
            await Refresh();
        }
    });
    private sealed record EffortChoice(string? Value, string Label);
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
        ScheduleButton.IsEnabled = conversation is not null && SendButton.IsEnabled && !string.IsNullOrWhiteSpace(Composer.Text);
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
        var clientMessageId = Guid.NewGuid().ToString("N");
        var optimistic = new DisplayItem($"local-{clientMessageId}", "userMessage", text, "", "pending", DateTimeOffset.UtcNow, clientMessageId);
        if (!optimisticItems.TryGetValue(id, out var pending)) optimisticItems[id] = pending = [];
        pending.Add(optimistic);
        conversation = conversation with { Items = conversation.Items.Append(optimistic).ToArray() };
        rendered = "";
        RenderTranscript(conversation);
        sending = true; SendButton.IsEnabled = false;
        var accepted = false;
        await Act(async () =>
        {
            var receipt = await relay.Services.GetRequiredService<ScheduledMessages>().Send(id, text, expectedTurn, null, clientMessageId);
            accepted = receipt.DeliveryStatus is "accepted" or "queued";
            if (accepted && optimisticItems.TryGetValue(id, out var acknowledged))
            {
                var index = acknowledged.FindIndex(item => item.ClientMessageId == clientMessageId);
                if (index >= 0) acknowledged[index] = acknowledged[index] with { Status = receipt.Queued ? "queued" : "accepted" };
            }
            if (receipt.Queued) ErrorDisplay.Text = receipt.Message;
            else if (!accepted) ErrorDisplay.Text = "This message's delivery is uncertain. Review the conversation before retrying.";
            if (accepted)
            {
                if (drafts.GetValueOrDefault(id) == text) drafts[id] = "";
                if (selected == id && Composer.Text == text) Composer.Clear();
            }
        });
        if (!accepted && optimisticItems.TryGetValue(id, out var failed)) failed.Remove(optimistic);
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
            if (item.Type is not "userMessage" and not "agentMessage")
            {
                RenderWorkItem(item);
                continue;
            }
            if (item.Type == "agentMessage") { content.Margin = new Thickness(8, 8, 8, 18); Transcript.Children.Add(content); }
            else Transcript.Children.Add(new Border { Child = content, Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(item.Type == "userMessage" ? "#2A3B2D" : "#19271F")), CornerRadius = new CornerRadius(8), HorizontalAlignment = item.Type == "userMessage" ? HorizontalAlignment.Right : HorizontalAlignment.Stretch, MaxWidth = item.Type == "userMessage" ? 760 : double.PositiveInfinity, Margin = new Thickness(0, 0, 0, 10) });
        }
        if (snapshot.Error is not null) Transcript.Children.Add(new TextBlock { Text = snapshot.Error, Foreground = Brushes.Salmon, Margin = new Thickness(12) });
        foreach (var request in snapshot.Requests) RenderRequest(snapshot.Id, request);
        if (atBottom) TranscriptScroll.ScrollToEnd();
    }
    private void RenderWorkItem(DisplayItem item)
    {
        var expanded = expandedWorkItems.Contains(item.Id);
        var summary = item.Detail.Replace("\r", "").Split('\n').FirstOrDefault(line => !string.IsNullOrWhiteSpace(line))?.Trim() ?? "No details";
        if (summary.Length > 180) summary = summary[..180] + "…";
        var header = new Button
        {
            Content = $"{(expanded ? "[-]" : "[+]" )} {item.Type} · {item.Status} · {summary}",
            HorizontalContentAlignment = HorizontalAlignment.Left,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#213128")),
            Foreground = Brushes.LightGreen,
            BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#47604B")),
            BorderThickness = new Thickness(1),
            ToolTip = "Expand or collapse work details"
        };
        var details = new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(item.Detail) ? "No additional details." : item.Detail,
            Margin = new Thickness(12, 4, 12, 12),
            TextWrapping = TextWrapping.Wrap,
            Foreground = Brushes.LightGreen,
            Visibility = expanded ? Visibility.Visible : Visibility.Collapsed
        };
        var panel = new StackPanel(); panel.Children.Add(header); panel.Children.Add(details);
        var card = new Border
        {
            Child = panel,
            Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#19271F")),
            BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#47604B")),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Margin = new Thickness(0, 0, 0, 10)
        };
        header.Click += (_, _) =>
        {
            if (expandedWorkItems.Add(item.Id))
            {
                details.Visibility = Visibility.Visible;
                header.Content = $"[-] {item.Type} · {item.Status} · {summary}";
            }
            else
            {
                expandedWorkItems.Remove(item.Id);
                details.Visibility = Visibility.Collapsed;
                header.Content = $"[+] {item.Type} · {item.Status} · {summary}";
            }
        };
        Transcript.Children.Add(card);
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
    private async void ScheduleMessage(object? sender, RoutedEventArgs e)
    {
        if (relay is null || conversation is null || string.IsNullOrWhiteSpace(Composer.Text)) { ErrorDisplay.Text = "Write a message before scheduling it."; return; }
        var id = conversation.Id;
        var text = Composer.Text;
        var date = new DatePicker { SelectedDate = DateTime.Today, Margin = new Thickness(0, 4, 0, 10) };
        var hours = new ComboBox { ItemsSource = Enumerable.Range(1, 12).ToArray(), SelectedItem = DateTime.Now.ToString("h") is string h && int.TryParse(h, out var hour) ? hour : 12, Width = 72 };
        var minutes = new ComboBox { ItemsSource = Enumerable.Range(0, 60).Select(value => value.ToString("D2")).ToArray(), SelectedItem = DateTime.Now.ToString("mm"), Width = 72 };
        var period = new ComboBox { ItemsSource = new[] { "AM", "PM" }, SelectedItem = DateTime.Now.ToString("tt", System.Globalization.CultureInfo.InvariantCulture), Width = 72 };
        var quick = new ComboBox { ItemsSource = new[] { "Choose date and time", "In 15 minutes", "In 1 hour", "Tomorrow at 9:00 AM" }, SelectedIndex = 0, Margin = new Thickness(0, 4, 0, 10) };
        var preview = new TextBox { Text = text, IsReadOnly = true, TextWrapping = TextWrapping.Wrap, MaxHeight = 100, Margin = new Thickness(0, 4, 0, 10) };
        var validation = new TextBlock { Foreground = Brushes.Salmon, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 8) };
        var schedule = new Button { Content = "Schedule message", IsDefault = true, HorizontalAlignment = HorizontalAlignment.Right };
        var dialog = new Window { Title = "Schedule message", Owner = this, Width = 560, SizeToContent = SizeToContent.Height, Content = null, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var panel = new StackPanel { Margin = new Thickness(20) };
        panel.Children.Add(new TextBlock { Text = "Message", FontWeight = FontWeights.SemiBold }); panel.Children.Add(preview);
        panel.Children.Add(new TextBlock { Text = $"Time ({TimeZoneInfo.Local.DisplayName})", FontWeight = FontWeights.SemiBold }); panel.Children.Add(quick);
        panel.Children.Add(date);
        var timeRow = new StackPanel { Orientation = Orientation.Horizontal };
        timeRow.Children.Add(hours); timeRow.Children.Add(new TextBlock { Text = ":", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 6, 0) }); timeRow.Children.Add(minutes); timeRow.Children.Add(period);
        panel.Children.Add(timeRow); panel.Children.Add(validation);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = new Button { Content = "Cancel", IsCancel = true }; buttons.Children.Add(cancel); buttons.Children.Add(schedule); panel.Children.Add(buttons);
        dialog.Content = panel;
        DateTime? SelectedLocal()
        {
            if (date.SelectedDate is null || hours.SelectedItem is not int selectedHour || minutes.SelectedItem is not string selectedMinute || period.SelectedItem is not string selectedPeriod) return null;
            var hour24 = selectedHour % 12 + (selectedPeriod == "PM" ? 12 : 0);
            return DateTime.SpecifyKind(date.SelectedDate.Value.Date.AddHours(hour24).AddMinutes(int.Parse(selectedMinute, System.Globalization.CultureInfo.InvariantCulture)), DateTimeKind.Local);
        }
        void Validate()
        {
            var local = SelectedLocal();
            if (local is null) { validation.Text = "Choose a date and time."; schedule.IsEnabled = false; return; }
            if (TimeZoneInfo.Local.IsInvalidTime(local.Value) || TimeZoneInfo.Local.IsAmbiguousTime(local.Value)) { validation.Text = "That local time is ambiguous or does not exist because of a daylight-saving transition."; schedule.IsEnabled = false; return; }
            if (local.Value <= DateTime.Now) { validation.Text = "Choose a future time."; schedule.IsEnabled = false; return; }
            validation.Text = $"Will send at {local.Value.ToString("f", System.Globalization.CultureInfo.CurrentCulture)}."; schedule.IsEnabled = true;
        }
        void SetQuick(DateTime local)
        {
            date.SelectedDate = local.Date; hours.SelectedItem = local.Hour % 12 == 0 ? 12 : local.Hour % 12; minutes.SelectedItem = local.Minute.ToString("D2"); period.SelectedItem = local.Hour >= 12 ? "PM" : "AM"; Validate();
        }
        quick.SelectionChanged += (_, _) => { if (quick.SelectedIndex == 1) SetQuick(DateTime.Now.AddMinutes(15)); else if (quick.SelectedIndex == 2) SetQuick(DateTime.Now.AddHours(1)); else if (quick.SelectedIndex == 3) SetQuick(DateTime.Today.AddDays(1).AddHours(9)); };
        date.SelectedDateChanged += (_, _) => Validate(); hours.SelectionChanged += (_, _) => Validate(); minutes.SelectionChanged += (_, _) => Validate(); period.SelectionChanged += (_, _) => Validate();
        schedule.Click += (_, _) => { Validate(); if (schedule.IsEnabled) dialog.DialogResult = true; };
        SetQuick(DateTime.Now.AddMinutes(15));
        if (dialog.ShowDialog() != true) return;
        await Act(async () =>
        {
            var local = SelectedLocal() ?? throw new InvalidOperationException("Choose a date and time.");
            await relay.Services.GetRequiredService<ScheduledMessages>().Schedule(id, text, new DateTimeOffset(local), null, Guid.NewGuid().ToString("N"));
            if (drafts.GetValueOrDefault(id) == text) drafts[id] = "";
            if (selected == id && Composer.Text == text) Composer.Clear();
            UpdateScheduledSummary(id);
            ErrorDisplay.Text = "Message scheduled. The desktop must be running at that time.";
        });
    }
    private void UpdateScheduledSummary(string? conversationId)
    {
        if (relay is null || conversationId is null)
        {
            ScheduledSummaryButton.Visibility = Visibility.Collapsed;
            return;
        }
        var pending = relay.Services.GetRequiredService<LocalStore>().Scheduled(conversationId).Count(job => job.Status == "pending");
        ScheduledSummaryButton.Content = pending == 1 ? "1 scheduled message" : $"{pending} scheduled messages";
        ScheduledSummaryButton.Visibility = pending == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private void ShowScheduled(object sender, RoutedEventArgs e)
    {
        if (relay is null || selected is null) return;
        var conversationId = selected;
        var messages = relay.Services.GetRequiredService<ScheduledMessages>();
        var panel = new StackPanel { Margin = new Thickness(16) };
        foreach (var job in relay.Services.GetRequiredService<LocalStore>().Scheduled(conversationId))
        {
            var row = new StackPanel { Margin = new Thickness(0, 0, 0, 16) };
            row.Children.Add(new TextBlock { Text = $"{job.Status} · {job.DueAt.LocalDateTime:g}\n{job.Text}\n{job.Error}", TextWrapping = TextWrapping.Wrap });
            if (job.Status == "pending")
            {
                var cancel = new Button { Content = "Cancel scheduled message", HorizontalAlignment = HorizontalAlignment.Left };
                cancel.Click += async (_, _) => await Act(async () =>
                {
                    await messages.Cancel(job.Id);
                    cancel.IsEnabled = false;
                    cancel.Content = "Canceled";
                    UpdateScheduledSummary(conversationId);
                });
                row.Children.Add(cancel);
            }
            panel.Children.Add(row);
        }
        if (panel.Children.Count == 0) panel.Children.Add(new TextBlock { Text = "No scheduled messages in this conversation." });
        new Window { Title = "Scheduled messages", Owner = this, Content = new ScrollViewer { Content = panel }, Width = 580, Height = 480, WindowStartupLocation = WindowStartupLocation.CenterOwner }.Show();
    }
}
