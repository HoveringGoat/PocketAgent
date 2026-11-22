using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
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
    private readonly Dictionary<string, FrameworkElement> renderedRows = [];
    private readonly Dictionary<string, DisplayItem> renderedRowState = [];
    private readonly HashSet<string> expandedWorkItems = [];
    private readonly HashSet<string> viewedImages = [];
    private ConversationSnapshot? conversation;
    private string? selected;
    private string rendered = "";
    private string sessionSignature = "";
    private string qrRendered = "";
    private bool refreshing, sending, settingDraft, changingList, closing, transitioning, loadingHistory;
    private DesktopUpdate? pendingUpdate;
    private (Version Version, bool Accepted)? queuedUpdateOffer;
    private readonly HashSet<Version> handledUpdateOffers = [];
    private bool offeringUpdate;
    private bool installingUpdate;
    private bool codexIdle;
    private CodexExecutable? nextCodexExecutable;
    private string? updateHealthFile;
    private string? expectedUpdateVersion;
    private string? providerModel;
    private string? renderedConversation;
    private bool followingLatest = true;
    private Conversations Conversations => relay!.Services.GetRequiredService<Conversations>();

    public MainWindow()
    {
        InitializeComponent();
        Title = $"PocketAgent Server {BuildInfo.Version}";
        ServerNameDisplay.Text = Environment.MachineName;
        BuildDisplay.Text = $"Desktop {BuildInfo.Version}\n{Environment.ProcessPath}";
        try { settings = DesktopSettings.Load(); }
        catch (Exception) { ErrorDisplay.Text = "Saved settings could not be read. Review and save the setup again."; }
        RestoreWindowPlacement();
        OpenSessionsOnStartup();
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
        CodexApiKeyVariableSetting.Text = settings.Host.CodexApiKeyEnvironmentVariable;
        UpdateCodexCredentialStatus();
        PermissionModeSetting.SelectedValue = settings.Host.PermissionMode;
        StartServerOnLaunchSetting.IsChecked = settings.StartServerOnLaunch;
        LaunchOnWindowsStartupSetting.IsChecked = settings.LaunchOnWindowsStartup;
        CheckUpdatesOnStartupSetting.IsChecked = settings.CheckUpdatesOnStartup;
        CheckUpdatesPeriodicallySetting.IsChecked = settings.CheckUpdatesPeriodically;
        AutomaticUpdatesSetting.IsChecked = settings.AutomaticUpdates;
        UpdateCodexExtensionOnStartupSetting.IsChecked = settings.UpdateCodexExtensionOnStartup;
        UiScaleSetting.SelectedValue = settings.UiScale;
        ApplyUiScale(settings.UiScale);
        HeartbeatSetting.IsChecked = settings.Host.EnableHeartbeats;
        AddressSetting.Text = settings.Host.ListenAddress;
        PortSetting.Text = settings.Host.Port.ToString(System.Globalization.CultureInfo.InvariantCulture);
        CertificateSetting.Text = settings.Host.CertificatePath;
        DirectOriginSetting.Text = settings.Host.DirectOrigin;
        LoadRelaySetup();
        if (settings.Host.RepositoryPath.Length == 0) Navigation.SelectedIndex = 2;
        ResolveCodex();
        UpdateNavigationButtons();
        timer.Tick += async (_, _) => { UpdateElapsedDisplay(); UpdateRelaySetup(); await Refresh(); };
        timer.Tick += async (_, _) => await OfferQueuedUpdate();
        timer.Start();
        updateTimer.Tick += async (_, _) => await CheckForUpdatesCore(false);
        updateTimer.Start();
        Loaded += async (_, _) => await WindowReady();
    }

    private void ResolveCodex()
    {
        try
        {
            nextCodexExecutable = CodexExecutableResolver.Resolve(relay?.Services.GetRequiredService<HostSettings>() ?? settings.Host);
            CodexResolved.Text = $"{nextCodexExecutable.Source}\n{nextCodexExecutable.Path}";
        }
        catch (Exception exception)
        {
            nextCodexExecutable = null;
            CodexResolved.Text = exception.Message;
        }
        UpdateCodexSwitch();
    }

    private static bool HasNewerCodexExecutable(HostSettings host, CodexExecutable? running, CodexExecutable? candidate) =>
        string.IsNullOrWhiteSpace(host.CodexExecutable) && running is not null && candidate?.ExtensionVersion is { } version &&
        !string.Equals(running.Path, candidate.Path, StringComparison.OrdinalIgnoreCase) &&
        (running.ExtensionVersion is null || version > running.ExtensionVersion);

    private void UpdateCodexSwitch()
    {
        var connection = relay?.Services.GetRequiredService<CodexConnection>();
        var active = connection?.Executable;
        var available = relay is not null && HasNewerCodexExecutable(relay.Services.GetRequiredService<HostSettings>(), active, nextCodexExecutable);
        CodexRestartButton.Visibility = available ? Visibility.Visible : Visibility.Collapsed;
        CodexRestartButton.IsEnabled = available && codexIdle && !transitioning && CodexUpdateCheckButton.IsEnabled;
        CodexVersionStatus.Visibility = active is null ? Visibility.Collapsed : Visibility.Visible;
        var runningLabel = connection is { IsReady: true } ? "Running" : "Last selected";
        CodexVersionStatus.Text = active is null ? "" : available
            ? $"{runningLabel}: {active.Source}. Available: {nextCodexExecutable!.Source}.\n{(codexIdle ? "Restart the PocketAgent server to switch." : "Waiting for active work to finish.")}"
            : $"{runningLabel}: {active.Source}.";
    }

    private void UpdateCodexCredentialStatus()
    {
        CodexApiKeyStatus.Text = !CodexCredentialStore.IsAvailable
            ? "Protected API-key storage is unavailable on this operating system."
            : settings.Host.CodexApiKeyOverride && CodexCredentialStore.Exists()
                ? $"A saved API-key override is present for {settings.Host.CodexApiKeyEnvironmentVariable}. The key value is hidden."
            : settings.Host.CodexApiKeyOverride
                ? "A saved API-key override is enabled, but no protected key was found. Enter a replacement before starting Codex."
                : $"No saved override. Codex may use {settings.Host.CodexApiKeyEnvironmentVariable} from the desktop environment or its own sign-in store.";
    }

    private void SelectNavigation(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string tag } && int.TryParse(tag, out var index)) Navigation.SelectedIndex = index;
    }

    private void NavigationSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!ReferenceEquals(e.Source, Navigation)) return;
        UpdateNavigationButtons();
        if (Navigation.SelectedIndex == 2 && CodexResolved is not null) ResolveCodex();
    }

    private void UpdateNavigationButtons()
    {
        if (SessionsNavButton is null) return;
        var selectedBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#2A4734"));
        var normalBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#18271F"));
        SessionsNavButton.Background = Navigation.SelectedIndex == 0 ? selectedBrush : normalBrush;
        DevicesNavButton.Background = Navigation.SelectedIndex == 1 ? selectedBrush : normalBrush;
        SettingsNavButton.Background = Navigation.SelectedIndex == 2 ? selectedBrush : normalBrush;
    }

    private async Task WindowReady()
    {
        if (Environment.GetCommandLineArgs().Contains("--background", StringComparer.OrdinalIgnoreCase)) WindowState = WindowState.Minimized;
        DesktopDiagnostics.Log($"desktop ready start_server_on_launch={settings.StartServerOnLaunch} repository_configured={settings.Host.RepositoryPath.Length > 0} saved_conversation={settings.LastSelectedConversationId is not null}");
        WriteUpdateHealth();
        try { StartupRegistration.SetEnabled(settings.LaunchOnWindowsStartup); }
        catch (Exception exception) { UpdateStatus.Text = $"Windows startup registration unavailable: {exception.Message}"; }
        if (settings.UpdateCodexExtensionOnStartup)
        {
            await CheckCodexExtensionUpdateCore();
            await SwitchToUpdatedCodex();
        }
        if (!closing && settings.StartServerOnLaunch && settings.Host.RepositoryPath.Length > 0)
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

    private void SaveConfiguration(bool removeRelayEnrollment = false)
    {
        if (relay is not null) throw new InvalidOperationException("Stop the server before changing settings.");
        var candidate = new DesktopSettings
        {
            StartServerOnLaunch = StartServerOnLaunchSetting.IsChecked == true,
            LaunchOnWindowsStartup = LaunchOnWindowsStartupSetting.IsChecked == true,
            CheckUpdatesOnStartup = CheckUpdatesOnStartupSetting.IsChecked == true,
            CheckUpdatesPeriodically = CheckUpdatesPeriodicallySetting.IsChecked == true,
            AutomaticUpdates = AutomaticUpdatesSetting.IsChecked == true,
            UpdateCodexExtensionOnStartup = UpdateCodexExtensionOnStartupSetting.IsChecked == true,
            UiScale = UiScaleSetting.SelectedValue as string ?? "comfortable",
            Host = new HostSettings
            {
                RepositoryPath = RepositorySetting.Text.Trim(),
                CodexExecutable = CodexSetting.Text.Trim(),
                CodexArguments = settings.Host.CodexArguments,
                CodexExtensionsDirectory = settings.Host.CodexExtensionsDirectory,
                CodexApiKeyEnvironmentVariable = CodexApiKeyVariableSetting.Text.Trim(),
                CodexApiKeyOverride = settings.Host.CodexApiKeyOverride || !string.IsNullOrWhiteSpace(CodexApiKeySetting.Password),
                ListenAddress = AddressSetting.Text.Trim(),
                Port = int.Parse(PortSetting.Text, System.Globalization.CultureInfo.InvariantCulture),
                CertificatePath = CertificateSetting.Text.Trim(),
                PublicOrigin = "",
                DirectOrigin = DirectOriginSetting.Text.Trim(),
                PinnedCertificate = settings.Host.PinnedCertificate && CertificateSetting.Text.Trim() == settings.Host.CertificatePath,
                EnableHeartbeats = HeartbeatSetting.IsChecked == true,
                PermissionMode = PermissionModeSetting.SelectedValue as string ?? "inherit",
                DataDirectory = DesktopSettings.DirectoryPath,
                Relay = ReadRelaySettings(removeRelayEnrollment)
            }
        };
        RuntimeHostSettings(candidate.Host).Validate();
        if (!string.IsNullOrWhiteSpace(CodexApiKeySetting.Password))
            CodexCredentialStore.Save(CodexApiKeySetting.Password);
        candidate.Save(); settings = candidate;
        CodexApiKeySetting.Clear();
        replacingRelayEnrollment = false;
        RelayEnrollmentSetting.Clear();
        UpdateRelaySetup();
        StartupRegistration.SetEnabled(candidate.LaunchOnWindowsStartup);
        updateTimer.IsEnabled = candidate.CheckUpdatesPeriodically;
        ResolveCodex();
        UpdateCodexCredentialStatus();
    }

    private async void SaveSettings(object sender, RoutedEventArgs e) => await Act(() => { SaveConfiguration(); ErrorDisplay.Text = "Settings saved."; return Task.CompletedTask; });
    private async void CheckCodexExtensionUpdate(object sender, RoutedEventArgs e) => await CheckCodexExtensionUpdateCore();

    private async Task CheckCodexExtensionUpdateCore(Func<IProgress<Version>, Task<CodexExtensionUpdateResult>>? check = null)
    {
        if (closing || !CodexUpdateCheckButton.IsEnabled) return;
        CodexUpdateCheckButton.IsEnabled = false;
        UpdateCodexSwitch();
        CodexUpdateStatus.Text = "Checking for Codex extension updates…";
        CodexUpdateProgress.Visibility = Visibility.Visible;
        var running = true;
        try
        {
            await Dispatcher.Yield(DispatcherPriority.Background);
            if (closing) return;
            if (!await CanInstallUpdate())
            {
                CodexUpdateStatus.Text = "Codex extension update waiting for all conversations to become idle.";
                return;
            }
            var progress = new Progress<Version>(version =>
            {
                if (running) CodexUpdateStatus.Text = $"Installing Codex extension {version}…";
            });
            var host = settings.Host;
            var result = await (check is null
                ? Task.Run(() => CodexExtensionUpdateService.CheckAsync(host, install: true, installationProgress: progress))
                : check(progress));
            CodexUpdateStatus.Text = result.Message;
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException or IOException or UnauthorizedAccessException or Win32Exception)
        {
            DesktopDiagnostics.LogException("codex-extension-update", exception);
            CodexUpdateStatus.Text = $"Codex extension update unavailable: {exception.Message}";
        }
        finally
        {
            running = false;
            CodexUpdateProgress.Visibility = Visibility.Collapsed;
            CodexUpdateCheckButton.IsEnabled = true;
            if (!closing) ResolveCodex();
        }
    }
    private void UiScaleChanged(object sender, SelectionChangedEventArgs e)
    {
        if (UiScaleSetting.SelectedValue is not string value) return;
        ApplyUiScale(value);
        if (IsLoaded)
        {
            settings.UiScale = value;
            settings.Save();
        }
    }
    private void ApplyUiScale(string? value)
    {
        var scale = value switch { "compact" => 0.9, "plus" => 1.1, "large" => 1.2, _ => 1.0 };
        UiScaleTransform.ScaleX = scale;
        UiScaleTransform.ScaleY = scale;
    }
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
        var apply = new Button { Content = "Save direct connection", Margin = new Thickness(16) };
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock { Text = "Use your router's public/WAN IPv4 address. No domain, tunnel, relay, or PocketAgent hosting is used. You must configure port forwarding and the firewall yourself.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(16) });
        panel.Children.Add(new TextBlock { Text = "Desktop LAN address (forward traffic here)", Margin = new Thickness(16, 0, 16, 0) }); panel.Children.Add(interfaces);
        panel.Children.Add(new TextBlock { Text = "Choose your physical Wi-Fi/Ethernet adapter, not a VPN or virtual adapter. For the first connection test, disconnect the VPN and use your router's WAN address; a VPN can change both detected IP and reply routing.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(16, 0, 16, 10) });
        panel.Children.Add(new TextBlock { Text = "Router public IPv4 address", Margin = new Thickness(16, 0, 16, 0) }); panel.Children.Add(publicIp); panel.Children.Add(detect); panel.Children.Add(detectionStatus);
        panel.Children.Add(new TextBlock { Text = $"Use TCP port {PortSetting.Text} for the router, this desktop listener, and Windows Firewall. Forward the router's public port {PortSetting.Text} to this desktop on the same port. After starting, scan the QR under Devices or copy its pairing string. Ports 80/443 and Caddy changes are not needed.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(16) }); panel.Children.Add(apply);
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
        var port = int.Parse(PortSetting.Text, System.Globalization.CultureInfo.InvariantCulture);
        var origin = $"https://{publicIp.Text.Trim()}:{port}";
        var candidate = new HostSettings { RepositoryPath = RepositorySetting.Text.Trim(), DataDirectory = DesktopSettings.DirectoryPath, ListenAddress = localIp, Port = port, DirectOrigin = origin, CertificatePath = Path.Combine(DesktopSettings.DirectoryPath, "certificates", "device", "server.pfx"), PinnedCertificate = true };
        candidate.Validate();
        using var setupLease = new HostLease(candidate); setupLease.Acquire();
        await Task.Run(() => PeerCertificates.Provision(candidate));
        AddressSetting.Text = localIp; CertificateSetting.Text = candidate.CertificatePath; DirectOriginSetting.Text = candidate.DirectOrigin;
        settings.Host.PinnedCertificate = true; settings.Host.CertificatePath = candidate.CertificatePath;
        SaveConfiguration();
        AppDialogs.Show(this, $"Direct connection saved: {candidate.ClientOrigin}\n\nForward public TCP {candidate.Port} to {localIp} TCP {candidate.Port}, and allow inbound TCP {candidate.Port} through Windows Firewall. Start the server, then use Devices to scan the pairing QR or copy its pairing string. No certificate installation or ports 80/443 are needed.", "Direct internet setup");
    });
    private async void CheckConnectivity(object sender, RoutedEventArgs e) => await Act(async () =>
    {
        if (relay is null) throw new InvalidOperationException("Start the server before running connection diagnostics.");
            var report = await relay.Services.GetRequiredService<ConnectivityDiagnostics>().Check(WindowsFirewall.IsConfigured);
        var text = new TextBox { Text = string.Join("\n\n", report.Checks), IsReadOnly = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(16) };
        new Window { Title = report.LocalConnectionVerified ? "Connection diagnostics - local HTTPS verified" : "Connection diagnostics - setup needs attention", Owner = this, Content = text, Width = 760, Height = 580, WindowStartupLocation = WindowStartupLocation.CenterOwner }.ShowDialog();
    });
    private async void ToggleServer(object sender, RoutedEventArgs e)
    {
        if (transitioning) return;
        if (relay is null)
        {
            await StartServerCore();
            return;
        }

        transitioning = true;
        ServerToggle.IsEnabled = false;
        ServerToggle.Content = "Stopping...";
        await Act(async () =>
        {
            if (await ConfirmStop()) await Shutdown();
            else SetServerToggle(true);
        });
        transitioning = false;
        if (relay is not null) SetServerToggle(true);
    }

    private async Task StartServerCore()
    {
        if (relay is not null || transitioning) return;
        transitioning = true; SetServerToggle(false, "Starting...", false);
        await Act(async () =>
        {
            SaveConfiguration();
            var host = RuntimeHostSettings(settings.Host);
            host.CodexApiKey = host.CodexApiKeyOverride ? CodexCredentialStore.Read() : null;
            if (IPAddress.TryParse(host.ListenAddress, out var listenAddress) && !IPAddress.IsLoopback(listenAddress) && !WindowsFirewall.IsConfigured(host.Port))
            {
                var choice = AppDialogs.Show(this, $"Allow inbound TCP {host.Port} through Windows Firewall? PocketAgent will create one port-based rule named '{WindowsFirewall.RuleName(host.Port)}'. It is tied to the port, not this version's executable path, so future updates will not prompt again. The rule is needed for inbound connections and can be removed from Windows Firewall later.", "Windows Firewall", MessageBoxButton.YesNo);
                if (choice == MessageBoxResult.Yes && !await Task.Run(() => WindowsFirewall.TryConfigure(host.Port)))
                    throw new InvalidOperationException("Windows Firewall rule was not created. The server was not started.");
            }
            await StartServer(host);
        });
        transitioning = false; SetServerToggle(relay is not null);
        UpdateCodexSwitch();
    }

    private async Task StartServer(HostSettings host)
    {
        var candidate = RelayHost.Create([], host);
        try { await candidate.StartAsync(); }
        catch { await candidate.DisposeAsync(); throw; }
        relay = candidate;
        sessionSignature = "";
        ConnectionAddress.Text = host.ClientOrigin;
        SetDevicesRelayClientMode(host.Relay.ConnectsUpstream);
        SetServerToggle(true, "Starting...", false); SaveButton.IsEnabled = false;
        PermissionModeSetting.IsEnabled = false;
        try
        {
            var status = await relay.Services.GetRequiredService<ProviderDiagnostics>().Check();
            providerModel = status.ConfiguredModel ?? status.DefaultModel;
        }
        catch { providerModel = null; }
        ResolveCodex();
        await Refresh();
    }

    private async void RestartUsingUpdatedCodex(object sender, RoutedEventArgs e) => await SwitchToUpdatedCodex();

    private async Task SwitchToUpdatedCodex()
    {
        if (relay is not { } running || transitioning || closing || installingUpdate || !CodexUpdateCheckButton.IsEnabled) return;
        ResolveCodex();
        var host = running.Services.GetRequiredService<HostSettings>();
        if (!HasNewerCodexExecutable(host, running.Services.GetRequiredService<CodexConnection>().Executable, nextCodexExecutable)) return;
        var expected = nextCodexExecutable!;
        transitioning = true;
        SetServerToggle(true, "Restarting…", false);
        UpdateCodexSwitch();
        try
        {
            if (!await running.Services.GetRequiredService<Conversations>().TryPrepareIdleRestart())
            {
                codexIdle = false;
                return;
            }
            CodexUpdateStatus.Text = $"Restarting PocketAgent to use {expected.Source}…";
            CodexUpdateProgress.Visibility = Visibility.Visible;
            CodexUpdateCheckButton.IsEnabled = false;
            await Shutdown();
            await StartServer(host);
            var connection = relay?.Services.GetRequiredService<CodexConnection>();
            CodexUpdateStatus.Text = connection is { IsReady: true } && connection.Executable?.Path.Equals(expected.Path, StringComparison.OrdinalIgnoreCase) == true
                ? $"Now using {expected.Source}."
                : "The server restarted, but could not confirm the updated Codex executable. Check the Codex connection for details.";
        }
        catch (Exception exception)
        {
            DesktopDiagnostics.LogException("codex-update-restart", exception);
            CodexUpdateStatus.Text = $"Could not restart with the updated Codex version: {exception.Message}";
        }
        finally
        {
            transitioning = false;
            SetServerToggle(relay is not null);
            CodexUpdateProgress.Visibility = Visibility.Collapsed;
            CodexUpdateCheckButton.IsEnabled = true;
            UpdateCodexSwitch();
        }
    }

    private async void CheckForUpdates(object sender, RoutedEventArgs e) => await CheckForUpdatesCore(false, true);

    private void ViewReleaseNotes(object sender, RoutedEventArgs e)
    {
        if (pendingUpdate is null) return;
        var notes = string.IsNullOrWhiteSpace(pendingUpdate.Notes) ? "No release notes were published for this version." : pendingUpdate.Notes;
        AppDialogs.Show(this, notes, $"PocketAgent {pendingUpdate.Version} release notes");
    }

    private async Task CheckForUpdatesCore(bool startup, bool userRequested = false, Func<Task<DesktopUpdate?>>? check = null)
    {
        if (closing || installingUpdate || offeringUpdate || !DesktopUpdateCheckButton.IsEnabled) return;
        if (startup && !settings.CheckUpdatesOnStartup) return;
        if (!startup && !userRequested && !settings.CheckUpdatesPeriodically) return;
        DesktopUpdateCheckButton.IsEnabled = false;
        DesktopUpdateInstallButton.IsEnabled = false;
        UpdateStatus.Text = "Checking for PocketAgent updates…";
        DesktopUpdateProgress.Visibility = Visibility.Visible;
        try
        {
            await Dispatcher.Yield(DispatcherPriority.Background);
            if (closing) return;
            var update = await (check is null ? UpdateService.CheckAsync() : check());
            DesktopUpdateProgress.Visibility = Visibility.Collapsed;
            pendingUpdate = update;
            if (queuedUpdateOffer?.Version != update?.Version || string.IsNullOrWhiteSpace(update?.Sha256)) queuedUpdateOffer = null;
            UpdateNotesButton.Visibility = update is null ? Visibility.Collapsed : Visibility.Visible;
            UpdateStatus.Text = update is null ? $"You are running the latest available desktop version ({BuildInfo.Version})." : $"Desktop update {update.Version} is available.";
            if (update is not null && (startup || settings.AutomaticUpdates) && !handledUpdateOffers.Contains(update.Version))
            {
                if (string.IsNullOrWhiteSpace(update.Sha256))
                    UpdateStatus.Text += " This release does not publish an installer checksum.";
                else
                    queuedUpdateOffer ??= (update.Version, false);
            }
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or JsonException)
        {
            UpdateNotesButton.Visibility = Visibility.Collapsed;
            UpdateStatus.Text = $"Update check unavailable: {exception.Message}";
        }
        finally
        {
            DesktopUpdateProgress.Visibility = Visibility.Collapsed;
            DesktopUpdateCheckButton.IsEnabled = true;
            DesktopUpdateInstallButton.IsEnabled = true;
        }
        await OfferQueuedUpdate();
    }

    private async Task OfferQueuedUpdate()
    {
        if (queuedUpdateOffer is not { } offer || pendingUpdate?.Version != offer.Version || offeringUpdate ||
            installingUpdate || transitioning || closing || !DesktopUpdateCheckButton.IsEnabled) return;
        offeringUpdate = true;
        try
        {
            if (!await CanInstallUpdate())
            {
                UpdateStatus.Text = $"Desktop update {offer.Version} is waiting for conversations to become idle.";
                return;
            }
            if (closing || transitioning || installingUpdate || queuedUpdateOffer != offer || !DesktopUpdateCheckButton.IsEnabled) return;
            if (!offer.Accepted && !settings.AutomaticUpdates)
            {
                if (!IsVisible || WindowState == WindowState.Minimized) return;
                var choice = AppDialogs.Show(this,
                    $"PocketAgent {offer.Version} is available. Install it now and restart PocketAgent?",
                    "PocketAgent update available", MessageBoxButton.YesNo, yesLabel: "Install update", noLabel: "Later");
                if (choice != MessageBoxResult.Yes)
                {
                    FinishUpdateOffer(offer.Version);
                    UpdateStatus.Text = $"Desktop update {offer.Version} is available to install from Settings.";
                    return;
                }
            }
            queuedUpdateOffer = (offer.Version, true);
            await InstallUpdateCore(false);
        }
        finally { offeringUpdate = false; }
    }

    private void FinishUpdateOffer(Version version)
    {
        handledUpdateOffers.Add(version);
        if (queuedUpdateOffer?.Version == version) queuedUpdateOffer = null;
    }

    private async void InstallUpdate(object sender, RoutedEventArgs e) => await InstallUpdateCore(true);

    private async Task InstallUpdateCore(bool userRequested)
    {
        if (installingUpdate || closing || transitioning) return;
        var version = pendingUpdate?.Version;
        installingUpdate = true;
        try
        {
            var deferred = await InstallPendingUpdate(userRequested);
            if (version is null) return;
            if (!deferred) FinishUpdateOffer(version);
            else if (queuedUpdateOffer?.Version == version) queuedUpdateOffer = (version, true);
        }
        finally { installingUpdate = false; }
    }

    private async Task<bool> InstallPendingUpdate(bool userRequested)
    {
        if (pendingUpdate is not { } update)
        {
            UpdateStatus.Text = "Check for updates before installing.";
            return false;
        }
        if (string.IsNullOrWhiteSpace(update.Sha256))
        {
            UpdateStatus.Text = "This release does not publish a checksum, so PocketAgent will not install it automatically.";
            return false;
        }
        if (!await CanInstallUpdate())
        {
            UpdateStatus.Text = "Update waiting for all conversations to become idle.";
            if (userRequested) AppDialogs.Show(this, UpdateStatus.Text, "Update waiting");
            return true;
        }
        if (closing) return false;
        if (transitioning) return true;
        var deferred = false;
        if (!UpdateDownloadDialog.Show(this, update, async () =>
        {
            deferred = !await CanInstallUpdate();
            return !deferred;
        }))
        {
            UpdateStatus.Text = deferred ? "Update waiting for all conversations to become idle." : "The update did not start.";
            return deferred;
        }
        closing = true;
        timer.Stop(); updateTimer.Stop();
        try { await Shutdown(); }
        finally { Close(); }
        return false;
    }

    private async Task<bool> CanInstallUpdate()
    {
        if (relay is not { } running) return true;
        var sessions = await running.Services.GetRequiredService<Conversations>().List();
        return relay == running && !sessions.Any(session => PocketAgent.Conversations.IsActiveStatus(session.Status));
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
        codexIdle = false;
        UpdateCodexSwitch();
        if (running is not null)
        {
            try { await running.StopAsync(); }
            finally { await running.DisposeAsync(); }
        }
        selected = null; conversation = null; rendered = ""; renderedConversation = null; JumpToLatestButton.Visibility = Visibility.Collapsed; SessionList.ItemsSource = null;
        providerModel = null;
        ConversationPanel.IsEnabled = false; ConversationActions.IsEnabled = false; InterruptButton.Visibility = Visibility.Hidden; ConversationLocation.Text = ""; ConversationMeta.Text = "No active conversation"; Transcript.Children.Clear(); Composer.Text = ""; ScheduledSummaryButton.Visibility = Visibility.Collapsed;
        PairingQr.Source = null; qrRendered = "";
        SetDevicesRelayClientMode(false);
        PairingDisplay.Text = ""; DeviceList.Children.Clear(); ConnectionAddress.Text = "";
        SetServerToggle(false); SaveButton.IsEnabled = true;
        PermissionModeSetting.IsEnabled = true;
    }
    private void SetServerToggle(bool running, string? label = null, bool enabled = true)
    {
        ServerToggle.IsChecked = running;
        ServerToggle.Content = label ?? (running ? "ON" : "OFF");
        ServerToggle.IsEnabled = enabled;
        UpdateRelaySetup();
    }

    private void SetDevicesRelayClientMode(bool client)
    {
        DevicesHeading.Text = client ? "Pair your phone through the relay" : "Connect your phone";
        if (!client) CertificateStatus.Text = "";
    }

    private void OpenSessionsOnStartup()
    {
        if (settings.Host.RepositoryPath.Length > 0) Navigation.SelectedIndex = 0;
    }

    private void RestoreWindowPlacement()
    {
        if (settings.WindowWidth is not { } width || settings.WindowHeight is not { } height ||
            width < MinWidth || height < MinHeight || double.IsNaN(width) || double.IsNaN(height)) return;
        Width = width;
        Height = height;
        if (settings.WindowLeft is not { } left || settings.WindowTop is not { } top || double.IsNaN(left) || double.IsNaN(top)) return;
        var visible = left + width > SystemParameters.VirtualScreenLeft &&
            left < SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth &&
            top + height > SystemParameters.VirtualScreenTop &&
            top < SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight;
        if (!visible) return;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = left;
        Top = top;
        if (settings.WindowMaximized) WindowState = WindowState.Maximized;
    }

    private void SaveWindowPlacement()
    {
        var bounds = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
        if (double.IsNaN(bounds.Left) || double.IsNaN(bounds.Top) || double.IsNaN(bounds.Width) || double.IsNaN(bounds.Height) ||
            bounds.Width < MinWidth || bounds.Height < MinHeight) return;
        settings.WindowLeft = bounds.Left;
        settings.WindowTop = bounds.Top;
        settings.WindowWidth = bounds.Width;
        settings.WindowHeight = bounds.Height;
        settings.WindowMaximized = WindowState == WindowState.Maximized;
        try { settings.Save(); }
        catch (Exception exception) { DesktopDiagnostics.LogException("window-placement-save", exception); }
    }

    private void RememberSelectedConversation(string? conversationId)
    {
        settings.LastSelectedConversationId = conversationId;
        try { settings.Save(); }
        catch (Exception exception) { DesktopDiagnostics.LogException("conversation-selection-save", exception); }
    }

    private string? RestoreLastSelectedConversation(ConversationSummary[] sessions)
    {
        var saved = settings.LastSelectedConversationId;
        if (string.IsNullOrWhiteSpace(saved)) return null;
        if (sessions.Any(session => session.Id == saved)) return saved;
        RememberSelectedConversation(null);
        return null;
    }

    private async void WindowClosing(object? sender, CancelEventArgs e)
    {
        if (closing) return;
        e.Cancel = true;
        if (transitioning) return;
        await Act(async () =>
        {
            if (!await ConfirmStop()) return;
            SaveWindowPlacement();
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
            var host = running.Services.GetRequiredService<HostSettings>();
            var clientRelay = running.Services.GetRequiredService<RelayRemoteHostService>();
            var isRelayClient = host.Relay.ConnectsUpstream;
            var canPair = isRelayClient ? clientRelay.SupportsPhonePairing &&
                host.Relay.UpstreamUrl.StartsWith("wss://", StringComparison.Ordinal) : host.UseHttps;
            CertificateStatus.Text = isRelayClient
                ? clientRelay.Status != RelayConnectionStatus.Connected ? "Connect to the relay to pair a phone."
                    : !clientRelay.SupportsPhonePairing ? "Update the relay node to enable phone pairing."
                    : !canPair ? "Set up HTTPS on the relay node and replace this desktop’s enrollment."
                    : "Scan this QR using Add server in PocketAgent on Android."
                : running.Services.GetRequiredService<PeerCertificates>().Status;
            DirectPairingPanel.Visibility = isRelayClient && !canPair ? Visibility.Collapsed : Visibility.Visible;
            var sessions = await Conversations.List();
            if (relay != running) return;
            codexIdle = !sessions.Any(session => PocketAgent.Conversations.IsActiveStatus(session.Status));
            UpdateCodexSwitch();
            var usage = await running.Services.GetRequiredService<ProviderUsage>().Check();
            if (relay != running) return;
            UsageDisplay.Text = usage.Available
                ? $"5hr: {UsageText(usage.FiveHour, true)} | weekly: {UsageText(usage.Weekly, false)}"
                : usage.UnavailableReason ?? "Usage limits unavailable.";
            UsageDisplay.ToolTip = usage.Available
                ? $"Five-hour reset: {usage.FiveHour?.ResetsAt?.LocalDateTime.ToString("g") ?? "unavailable"}\nWeekly reset: {usage.Weekly?.ResetsAt?.LocalDateTime.ToString("g") ?? "unavailable"}"
                : usage.UnavailableReason ?? "Usage limits are unavailable.";
            var signature = JsonSerializer.Serialize(sessions);
            if (selected is not null && !sessions.Any(session => session.Id == selected))
            {
                selected = null; conversation = null; rendered = ""; renderedConversation = null; JumpToLatestButton.Visibility = Visibility.Collapsed;
                ConversationPanel.IsEnabled = false; ConversationActions.IsEnabled = false; InterruptButton.Visibility = Visibility.Hidden; ConversationLocation.Text = ""; ConversationMeta.Text = "No active conversation"; Transcript.Children.Clear(); ScheduledSummaryButton.Visibility = Visibility.Collapsed;
                settingDraft = true; Composer.Clear(); settingDraft = false;
                RememberSelectedConversation(null);
            }
            var hadNoSelection = selected is null;
            selected ??= RestoreLastSelectedConversation(sessions);
            if (hadNoSelection && selected is not null)
                DesktopDiagnostics.Log($"conversation selection restored sessions={sessions.Length}");
            else if (sessionSignature.Length == 0)
                DesktopDiagnostics.Log($"conversation sessions loaded count={sessions.Length} selected={selected is not null}");
            if (signature != sessionSignature)
            {
                changingList = true;
                SessionList.ItemsSource = sessions.Select(session => new SessionEntry(session.Id, $"{session.Title}\n{session.Status}")).ToArray();
                SessionList.SelectedItem = SessionList.Items.Cast<SessionEntry>().FirstOrDefault(item => item.Id == selected);
                changingList = false; sessionSignature = signature;
            }
            var code = running.Services.GetRequiredService<PairingCode>().Current;
            PairingDisplay.Text = !canPair ? isRelayClient ? "" : "Set up HTTPS to pair a phone" :
                code.Consumed ? "Pairing used — generate a new QR" : code.ExpiresAt <= DateTimeOffset.UtcNow ? "Pairing expired — generate a new QR" : "Scan or copy to pair";
            PairingExpiry.Text = canPair ? $"Single use · expires {code.ExpiresAt.LocalDateTime:t}" : "";
            var pairing = running.Services.GetRequiredService<PairingCode>();
            if (!canPair || code.Consumed || code.ExpiresAt <= DateTimeOffset.UtcNow) { PairingQr.Source = null; qrRendered = ""; }
            else
            {
                var payload = isRelayClient ? GetRelayPhonePairingPayload() : PeerCertificates.PairingPayload(host, pairing.Ticket);
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
            ConversationActions.IsEnabled = true;
            ServerNameDisplay.Text = snapshot.HostName;
            ConversationLocation.Text = snapshot.WorkingDirectory;
            var effectiveModel = snapshot.Model ?? providerModel;
            var modelLabel = effectiveModel is null ? "Codex settings" : effectiveModel;
            var reasoningLabel = snapshot.ReasoningEffort ?? "model default";
            ModelButton.ToolTip = $"Model: {modelLabel}\nReasoning: {reasoningLabel}";
            ModelButton.IsEnabled = snapshot.ActiveTurnId is null && snapshot.Status is not ("starting" or "uncertain" or "disconnected") && !sending;
            ConversationMeta.Text = FormatConversationMeta(snapshot);
            ConversationMetaSurface.BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(snapshot.ActiveTurnId is null ? "#47604B" : "#A7BAA7"));
            InterruptButton.IsEnabled = snapshot.ActiveTurnId is not null;
            InterruptButton.Visibility = snapshot.ActiveTurnId is null ? Visibility.Hidden : Visibility.Visible;
            SendButton.Content = "Send";
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
        ConversationMeta.Text = FormatConversationMeta(conversation);
    }

    private string FormatConversationMeta(ConversationSnapshot snapshot)
    {
        var work = snapshot.ActiveTurnStartedAt is { } start ? $" - {Elapsed((long)(DateTimeOffset.UtcNow - start).TotalMilliseconds)}" :
            snapshot.LastWorkDurationMs is { } duration ? $" - worked {Elapsed(duration)}" : "";
        var status = string.IsNullOrWhiteSpace(snapshot.Status) ? "idle" : snapshot.Status;
        var modelLabel = snapshot.Model ?? "Codex";
        var reasoningLabel = snapshot.ReasoningEffort ?? "default";
        return $"{status}{work} - {modelLabel} {reasoningLabel}";
    }


    private void RenderDevices()
    {
        DeviceList.Children.Clear();
        var store = relay!.Services.GetRequiredService<LocalStore>();
        DeviceAccessStatus.Text = "Open Edit conversation to manage device access. Revoke device removes the device entirely.";
        foreach (var device in store.Devices())
        {
            var row = new DockPanel { Margin = new Thickness(0, 5, 0, 5) };
            var revoke = new Button { Content = "Revoke device" }; DockPanel.SetDock(revoke, Dock.Right);
            revoke.Click += (_, _) => { store.Revoke(device.Id); RenderDevices(); };
            row.Children.Add(revoke); row.Children.Add(new TextBlock { Text = $"{device.Name}\nID {device.Id}\nExpires {device.ExpiresAt.LocalDateTime:d}", VerticalAlignment = VerticalAlignment.Center });
            DeviceList.Children.Add(row);
        }
    }
    private Task ShowConversationAccess(string conversationId, string title, Window owner)
    {
        var store = relay!.Services.GetRequiredService<LocalStore>();
        var devices = store.Devices();
        var panel = new StackPanel { Margin = new Thickness(18) };
        panel.Children.Add(new TextBlock { Text = $"Choose which paired devices can access \"{title}\".", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12) });
        var checks = new List<(Device Device, CheckBox Check)>();
        foreach (var device in devices)
        {
            var check = new CheckBox
            {
                Content = $"{device.Name}\nID {device.Id}\nExpires {device.ExpiresAt.LocalDateTime:d}",
                IsChecked = store.HasConversationAccess(conversationId, device.Id),
                Margin = new Thickness(0, 5, 0, 5),
                Padding = new Thickness(4)
            };
            checks.Add((device, check));
            panel.Children.Add(check);
        }
        if (checks.Count == 0)
            panel.Children.Add(new TextBlock { Text = "No paired devices yet. Pair a device from the Devices page first.", Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#A7BAA7")), TextWrapping = TextWrapping.Wrap });
        var save = new Button { Content = "Done", IsDefault = true, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
        panel.Children.Add(save);
        var dialog = new Window { Title = $"Conversation access - {title}", Owner = owner, Content = new ScrollViewer { Content = panel }, Width = 520, MaxHeight = owner.Owner?.ActualHeight ?? ActualHeight, SizeToContent = SizeToContent.Height, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        save.Click += async (_, _) =>
        {
            foreach (var (device, check) in checks)
            {
                var granted = check.IsChecked == true;
                var current = store.HasConversationAccess(conversationId, device.Id);
                if (granted == current) continue;
                if (granted) store.GrantConversationAccess(conversationId, device.Id);
                else store.RevokeConversationAccess(conversationId, device.Id);
            }
            await relay.Services.GetRequiredService<ProgressPublisher>().Changed(conversationId);
            dialog.DialogResult = true;
        };
        dialog.ShowDialog();
        return Task.CompletedTask;
    }
    private async void SessionSelected(object sender, SelectionChangedEventArgs e)
    {
        if (changingList || SessionList.SelectedItem is not SessionEntry entry) return;
        selected = entry.Id; conversation = null; rendered = ""; InterruptButton.Visibility = Visibility.Hidden; ConversationMeta.Text = "Loading conversation..."; historyCursors.Remove(selected); olderHistory.Remove(selected);
        RememberSelectedConversation(selected);
        settingDraft = true; Composer.Text = drafts.GetValueOrDefault(selected, ""); settingDraft = false;
        await Refresh();
    }

    private async void TranscriptScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (!ReferenceEquals(e.OriginalSource, TranscriptScroll)) return;
        if (e.ViewportHeightChange != 0)
            foreach (var row in Transcript.Children.OfType<Button>())
                if (row.Tag is TextBlock && row.Content is StackPanel panel && panel.Children[0] is Image preview)
                    preview.MaxHeight = PreviewHeight;
        var nearLatest = TranscriptScroll.ScrollableHeight - TranscriptScroll.VerticalOffset <= 40;
        if (e.VerticalChange < 0 || e.VerticalChange > 0 && nearLatest)
            followingLatest = nearLatest;
        if (followingLatest && !nearLatest) TranscriptScroll.ScrollToEnd();
        JumpToLatestButton.Visibility = renderedConversation is not null && !followingLatest && !nearLatest ? Visibility.Visible : Visibility.Collapsed;
        if (loadingHistory || selected is not { } id || conversation?.HistoryBefore is not { } before ||
            relay is not { } host || TranscriptScroll.ScrollableHeight <= 0 || e.VerticalChange >= 0 || TranscriptScroll.VerticalOffset > 24) return;
        loadingHistory = true;
        var previousExtent = TranscriptScroll.ExtentHeight;
        var previousOffset = TranscriptScroll.VerticalOffset;
        try
        {
            var page = await Conversations.History(id, before, 50);
            if (selected != id || relay != host || conversation is null) return;
            if (!olderHistory.TryGetValue(id, out var items)) olderHistory[id] = items = [];
            var known = items.Select(item => item.Id).Concat(conversation.Items.Select(item => item.Id)).ToHashSet(StringComparer.Ordinal);
            items.InsertRange(0, page.Items.Where(item => known.Add(item.Id)));
            historyCursors[id] = page.Before;
            await Refresh();
            _ = Dispatcher.BeginInvoke(() =>
            {
                if (selected == id && !followingLatest)
                    TranscriptScroll.ScrollToVerticalOffset(Math.Max(0, previousOffset + TranscriptScroll.ExtentHeight - previousExtent));
            }, DispatcherPriority.Loaded);
        }
        catch (Exception) { ErrorDisplay.Text = "Earlier messages could not be loaded. Scroll up again to retry."; }
        finally { loadingHistory = false; }
    }

    private void JumpToLatest(object sender, RoutedEventArgs e)
    {
        followingLatest = true;
        TranscriptScroll.ScrollToEnd();
        JumpToLatestButton.Visibility = Visibility.Collapsed;
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
        var created = await Conversations.Create("", directory.Text, ((ModelChoice)choices.SelectedItem).Model); selected = created.Id; RememberSelectedConversation(selected); conversation = created; rendered = "";
        settingDraft = true; Composer.Text = ""; settingDraft = false;
        await Refresh(); Composer.Focus();
    });
    private sealed record ModelChoice(string? Model, string DisplayName);
    private async void ChangeModel(object sender, RoutedEventArgs e) => await Act(async () =>
    {
        if (relay is null || conversation is null) return;
        var id = conversation.Id;
        var models = await relay.Services.GetRequiredService<ProviderDiagnostics>().Models();
        var search = new TextBox { ToolTip = "Search by model name or ID", Margin = new Thickness(16, 8, 16, 4) };
        var choices = new ListBox
        {
            Margin = new Thickness(16, 4, 16, 8),
            Height = 260,
            BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#47604B")),
            ItemTemplate = (DataTemplate)FindResource("ModelOptionTemplate")
        };
        var selectedModelId = conversation.Model;
        choices.ItemsSource = models;
        choices.SelectedItem = models.FirstOrDefault(model => model.Model == selectedModelId);
        var effort = new ComboBox { Margin = new Thickness(16, 4, 16, 8), MinHeight = 38 };
        void RefreshEffortChoices()
        {
            var selected = choices.SelectedItem as ModelOption;
            var options = new[] { new EffortChoice(null, "Use model default") }.Concat((selected?.SupportedReasoningEfforts ?? []).Select(value => new EffortChoice(value, ReasoningLabel(value)))).ToArray();
            effort.ItemsSource = options;
            effort.DisplayMemberPath = "Label";
            effort.SelectedIndex = Array.FindIndex(options, option => string.Equals(option.Value, conversation.ReasoningEffort, StringComparison.OrdinalIgnoreCase));
            if (effort.SelectedIndex < 0) effort.SelectedIndex = 0;
        }
        choices.SelectionChanged += (_, _) =>
        {
            if (choices.SelectedItem is ModelOption selected) selectedModelId = selected.Model;
            RefreshEffortChoices();
        };
        search.TextChanged += (_, _) =>
        {
            var query = search.Text.Trim();
            choices.ItemsSource = string.IsNullOrWhiteSpace(query)
                ? models
                : models.Where(model => model.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase) || model.Model.Contains(query, StringComparison.OrdinalIgnoreCase)).ToArray();
            choices.SelectedItem = models.FirstOrDefault(model => model.Model == selectedModelId && (string.IsNullOrWhiteSpace(query) || model.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase) || model.Model.Contains(query, StringComparison.OrdinalIgnoreCase)));
        };
        RefreshEffortChoices();
        var save = new Button { Content = "Use selected model", Margin = new Thickness(16), IsDefault = true };
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock { Text = "Choose the model for future messages and scheduled work.", Margin = new Thickness(16, 16, 16, 4) });
        panel.Children.Add(new TextBlock { Text = "Search models", Margin = new Thickness(16, 8, 16, 0) });
        panel.Children.Add(search);
        panel.Children.Add(choices);
        panel.Children.Add(new TextBlock { Text = "Reasoning", Margin = new Thickness(16, 4, 16, 0) });
        panel.Children.Add(effort);
        panel.Children.Add(save);
        var dialog = new Window { Title = "Choose conversation model", Owner = this, Content = panel, Width = 560, SizeToContent = SizeToContent.Height, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        save.Click += (_, _) => { if (choices.SelectedItem is not null) dialog.DialogResult = true; };
        if (dialog.ShowDialog() == true && choices.SelectedItem is ModelOption selected && effort.SelectedItem is EffortChoice selectedEffort)
        {
            await Conversations.SetModel(id, selected.Model);
            await Conversations.SetReasoning(id, selectedEffort.Value);
            await Refresh();
        }
    });
    private static string ReasoningLabel(string value) => value.ToLowerInvariant() switch
    {
        "xhigh" => "Extra high",
        "high" => "High",
        "medium" => "Medium",
        "low" => "Low",
        _ => value
    };
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
    private async void EditConversation(object sender, RoutedEventArgs e)
    {
        if (conversation is not { } current || relay is null) return;
        var (dialog, input, pinned) = CreateConversationEditor(current);
        if (dialog.ShowDialog() == true) await Act(async () =>
        {
            if (input.Text.Trim() != current.Title) await Conversations.Rename(current.Id, input.Text);
            if ((pinned.IsChecked == true) != current.Pinned) await Conversations.SetPinned(current.Id, pinned.IsChecked == true);
            await Refresh();
        });
    }

    private (Window Dialog, TextBox Name, CheckBox Pinned) CreateConversationEditor(ConversationSnapshot current)
    {
        var input = new TextBox { Text = current.Title, MaxLength = 100 };
        AutomationProperties.SetName(input, "Conversation name");
        var pinned = new CheckBox { Content = "Pin conversation", IsChecked = current.Pinned, Margin = new Thickness(0, 0, 0, 12) };
        var access = new Button { Content = "Manage device access", HorizontalAlignment = HorizontalAlignment.Left };
        var save = new Button { Content = "Save", IsDefault = true, HorizontalAlignment = HorizontalAlignment.Right };
        var panel = new StackPanel { Margin = new Thickness(16) };
        panel.Children.Add(new TextBlock { Text = "Conversation name" });
        panel.Children.Add(input);
        panel.Children.Add(pinned);
        panel.Children.Add(access);
        panel.Children.Add(save);
        var dialog = new Window { Title = "Edit conversation", Owner = this, Content = panel, Width = 440, SizeToContent = SizeToContent.Height, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        access.Click += async (_, _) => await Act(() => ShowConversationAccess(current.Id, current.Title, dialog));
        save.Click += (_, _) => dialog.DialogResult = true;
        return (dialog, input, pinned);
    }

    private void RenderTranscript(ConversationSnapshot snapshot)
    {
        if (renderedConversation != snapshot.Id)
        {
            renderedConversation = snapshot.Id;
            followingLatest = true;
            JumpToLatestButton.Visibility = Visibility.Collapsed;
            expandedWorkItems.Clear();
            imagePreviews.Clear();
            renderedRowState.Clear();
        }
        if (TryPatchRenderedRows(snapshot))
        {
            if (followingLatest) TranscriptScroll.ScrollToEnd();
            return;
        }
        Transcript.Children.Clear();
        renderedRows.Clear();
        renderedRowState.Clear();
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
        for (var index = start; index < snapshot.Items.Length; index++)
        {
            var item = snapshot.Items[index];
            if (item.Type is "userMessage" or "agentMessage" && item.Timestamp is { } timestamp)
            {
                var minute = timestamp.LocalDateTime.ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture);
                if (minute != lastMinute) Transcript.Children.Add(new TextBlock { Text = timestamp.LocalDateTime.ToString("t"), FontSize = 11, Foreground = Brushes.DarkSeaGreen, Margin = new Thickness(8, 5, 0, 5) });
                lastMinute = minute;
            }
            if (item.Type is not "userMessage" and not "agentMessage")
            {
                renderedRowState[item.Id] = item;
                if (item.Images is { Length: > 0 } images)
                {
                    foreach (var image in images) RenderImage(item, image, snapshot);
                }
                else if (item.Type.Contains("image", StringComparison.OrdinalIgnoreCase) || item.Type.Contains("screenshot", StringComparison.OrdinalIgnoreCase))
                    Transcript.Children.Add(new TextBlock { Text = "Image unavailable. The agent did not provide an image source.", Margin = new Thickness(8, 4, 8, 8) });
                else if (RoutineActivity(item) is { } action)
                {
                    var end = index + 1;
                    while (end < snapshot.Items.Length && RoutineActivity(snapshot.Items[end]) == action) end++;
                    var group = snapshot.Items[index..end];
                    foreach (var member in group) renderedRowState[member.Id] = member;
                    RenderActivityGroup(group, action);
                    index = end - 1;
                }
                else RenderWorkItem(item, Transcript);
                continue;
            }
            renderedRowState[item.Id] = item;
            if (item.Type == "agentMessage")
            {
                var content = new StackPanel { Margin = new Thickness(8, 8, 8, 12) };
                TranscriptMarkdown.Render(content, item.Text);
                renderedRows[item.Id] = content;
                Transcript.Children.Add(content);
            }
            else
            {
                var content = TranscriptMarkdown.ReadOnlyText(item.Text);
                content.Margin = new Thickness(12);
                renderedRows[item.Id] = content;
                Transcript.Children.Add(new Border { Child = content, Background = Brushes.Transparent, CornerRadius = new CornerRadius(8), HorizontalAlignment = HorizontalAlignment.Right, MaxWidth = 760, Margin = new Thickness(0, 0, 0, 10) });
            }
            foreach (var image in item.Images ?? []) RenderImage(item, image, snapshot);
        }
        if (snapshot.Error is not null)
        {
            var error = TranscriptMarkdown.ReadOnlyText(snapshot.Error, Brushes.Salmon);
            error.Tag = "transcript-notice";
            error.Margin = new Thickness(12);
            Transcript.Children.Add(error);
        }
        foreach (var request in snapshot.Requests) RenderRequest(snapshot.Id, request);
        if (followingLatest) TranscriptScroll.ScrollToEnd();
    }

    private bool TryPatchRenderedRows(ConversationSnapshot snapshot)
    {
        if (renderedRowState.Count == 0 || snapshot.Requests.Length > 0 || snapshot.Error is not null ||
            Transcript.Children.OfType<FrameworkElement>().Any(element => Equals(element.Tag, "transcript-notice"))) return false;
        var visible = snapshot.Items.Length > MaxRenderedItems ? snapshot.Items[^MaxRenderedItems..] : snapshot.Items;
        if (!visible.Select(item => item.Id).SequenceEqual(renderedRowState.Keys) ||
            visible.Sum(item => (long)item.Text.Length + item.Detail.Length) > MaxRenderedCharacters) return false;
        foreach (var item in visible)
        {
            var previous = renderedRowState[item.Id];
            if (previous.Type != item.Type || previous.Timestamp != item.Timestamp ||
                !(previous.Images ?? []).SequenceEqual(item.Images ?? [])) return false;
            if (item.Type is not ("userMessage" or "agentMessage"))
            {
                if (previous.Detail != item.Detail || previous.Status != item.Status || previous.Text != item.Text) return false;
                continue;
            }
            if (previous.Text != item.Text)
            {
                if (renderedRows[item.Id] is TextBox text) text.Text = item.Text;
                else TranscriptMarkdown.Render((StackPanel)renderedRows[item.Id], item.Text);
            }
            renderedRowState[item.Id] = item;
        }
        return true;
    }

    private readonly Dictionary<string, Task<BitmapImage>> imagePreviews = [];
    private readonly SemaphoreSlim previewSlots = new(2);
    private double PreviewHeight => Math.Clamp(TranscriptScroll.ViewportHeight - 44, 24, 152);

    private void RenderImage(DisplayItem item, DisplayImage image, ConversationSnapshot snapshot)
    {
        var label = ImageLabel(image);
        var imageKey = $"{item.Id}:{image.Source}";
        var caption = new TextBlock { Text = viewedImages.Contains(imageKey) ? $"Viewed image: {label}" : label, TextWrapping = TextWrapping.Wrap };
        var thumbnail = new Image { MaxWidth = 240, MaxHeight = PreviewHeight, Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Left, Visibility = Visibility.Collapsed };
        var content = new StackPanel();
        content.Children.Add(thumbnail);
        content.Children.Add(caption);
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = Brushes.DarkSeaGreen, Margin = new Thickness(12, 0, 8, 8), Visibility = Visibility.Collapsed };
        var button = new Button
        {
            Content = content,
            Tag = caption,
            ToolTip = "Open full image",
            HorizontalAlignment = HorizontalAlignment.Left,
            Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#C1DAA6")),
            Margin = new Thickness(8, 0, 8, 4)
        };
        AutomationProperties.SetName(button, $"Open image: {label}");
        button.Click += async (_, _) =>
        {
            if (renderedConversation == snapshot.Id) await ShowImage(item, image, imageKey, button, label);
        };
        button.Loaded += async (_, _) =>
        {
            if (thumbnail.Source is not null || status.Text.Length > 0) return;
            status.Text = "Loading preview…";
            status.Visibility = Visibility.Visible;
            try
            {
                if (!imagePreviews.TryGetValue(image.Source, out var pending))
                {
                    if (imagePreviews.Count >= 32) imagePreviews.Remove(imagePreviews.Keys.First());
                    pending = LoadPreview(snapshot, image.Source);
                    imagePreviews.Add(image.Source, pending);
                }
                thumbnail.Source = await pending;
                thumbnail.Visibility = Visibility.Visible;
                status.Visibility = Visibility.Collapsed;
            }
            catch (Exception exception) { status.Text = $"Preview unavailable. {exception.Message}"; }
        };
        Transcript.Children.Add(button);
        Transcript.Children.Add(status);
    }

    private static string ImageLabel(DisplayImage image)
    {
        if (!string.IsNullOrWhiteSpace(image.AltText)) return image.AltText;
        if (image.Source.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) return "Image";
        var path = Uri.TryCreate(image.Source, UriKind.Absolute, out var uri) ? uri.LocalPath : image.Source;
        var name = Path.GetFileName(Uri.UnescapeDataString(path));
        return string.IsNullOrWhiteSpace(name) ? "Image" : name;
    }

    private async Task<BitmapImage> LoadPreview(ConversationSnapshot snapshot, string source)
    {
        var host = relay;
        await previewSlots.WaitAsync();
        try
        {
            if (renderedConversation != snapshot.Id) throw new InvalidOperationException("Open the image from its conversation.");
            var bytes = await LoadImageBytes(source, snapshot, host);
            return await Task.Run(() => DecodeBitmap(bytes, 320));
        }
        finally { previewSlots.Release(); }
    }

    private async Task ShowImage(DisplayItem item, DisplayImage image, string imageKey, Button trigger, string label)
    {
        try
        {
            var bytes = await LoadImageBytes(image.Source, conversation, relay);
            BitmapImage? bitmap;
            try { bitmap = await Task.Run(() => DecodeImage(bytes)); }
            catch (Exception exception) when (exception is InvalidOperationException or NotSupportedException or FileFormatException or ArgumentException)
            {
                ShowUnsupportedImage(image, imageKey, trigger, label, bytes);
                return;
            }
            viewedImages.Add(imageKey);
            SetViewedImageLabel(trigger, label);
            var panel = new Grid { Margin = new Thickness(16) };
            panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            panel.RowDefinitions.Add(new RowDefinition());
            panel.Children.Add(new TextBlock { Text = $"{label} · {bitmap.PixelWidth} × {bitmap.PixelHeight} px", Foreground = Brushes.LightGreen, Margin = new Thickness(0, 0, 0, 10), MaxHeight = 80, TextTrimming = TextTrimming.CharacterEllipsis });
            var viewer = new Image { Source = bitmap, Stretch = Stretch.Uniform };
            var dialog = new Window
            {
                Title = "Image", Owner = this, Width = Math.Min(900, SystemParameters.WorkArea.Width),
                Height = Math.Min(760, SystemParameters.WorkArea.Height), MinWidth = 360, MinHeight = 260,
                WindowStartupLocation = WindowStartupLocation.CenterOwner, Content = panel
            };
            var expand = new Button { Content = "Expand", Margin = new Thickness(0, 0, 8, 10), Padding = new Thickness(12, 5, 12, 5) };
            expand.Click += (_, _) =>
            {
                var expanded = dialog.WindowState == WindowState.Maximized;
                dialog.WindowState = expanded ? WindowState.Normal : WindowState.Maximized;
                expand.Content = expanded ? "Expand" : "Restore";
            };
            var external = new Button { Content = "Open in external viewer", Margin = new Thickness(0, 0, 8, 10), Padding = new Thickness(12, 5, 12, 5) };
            external.Click += (_, _) => { OpenExternalImage(image, bytes); external.Content = "Opened externally"; };
            var actions = new WrapPanel();
            actions.Children.Add(expand);
            actions.Children.Add(external);
            Grid.SetRow(actions, 1);
            panel.Children.Add(actions);
            Grid.SetRow(viewer, 2);
            panel.Children.Add(viewer);
            dialog.ShowDialog();
        }
        catch (Exception exception) { ErrorDisplay.Text = $"Image could not be viewed: {exception.Message}"; }
    }

    private async Task<byte[]> LoadImageBytes(string source, ConversationSnapshot? snapshot, WebApplication? host)
    {
        var directory = snapshot?.WorkingDirectory ?? settings.Host.RepositoryPath;
        if (source.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            return await Task.Run(() => ReadImageBytes(source, directory));
        if (host is not null && snapshot is not null)
        {
            var image = await host.Services.GetRequiredService<Conversations>().ReadImage(snapshot.Id, source);
            return image.Bytes;
        }
        return await Task.Run(() => ReadImageBytes(source, directory));
    }

    private static void SetViewedImageLabel(Button trigger, string label)
    {
        if (trigger.Tag is TextBlock caption) caption.Text = $"Viewed image: {label}";
        else trigger.Content = $"Viewed image: {label}";
    }

    private void ShowUnsupportedImage(DisplayImage image, string imageKey, Button trigger, string label, byte[] bytes)
    {
        viewedImages.Add(imageKey);
        SetViewedImageLabel(trigger, label);
        var open = new Button { Content = "Open in external viewer", Margin = new Thickness(0, 10, 0, 0), Padding = new Thickness(12, 5, 12, 5) };
        var dialog = new Window { Title = "Image", Owner = this, Width = 520, SizeToContent = SizeToContent.Height, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        open.Click += (_, _) => { OpenExternalImage(image, bytes); open.Content = "Opened externally"; };
        var panel = new StackPanel { Margin = new Thickness(16) };
        panel.Children.Add(new TextBlock { Text = $"Viewed image: {label}", Foreground = Brushes.LightGreen });
        panel.Children.Add(new TextBlock { Text = "This image format is not supported by the embedded desktop preview. Open it in the Windows default viewer.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0) });
        panel.Children.Add(open);
        dialog.Content = panel;
        dialog.ShowDialog();
    }

    private static void OpenExternalImage(DisplayImage image, byte[] bytes)
    {
        var extension = ImageExtension(image);
        var path = Path.Combine(Path.GetTempPath(), $"PocketAgent-image-{Guid.NewGuid():N}{extension}");
        File.WriteAllBytes(path, bytes);
        Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
    }

    private static byte[] ReadImageBytes(string source, string workingDirectory)
    {
        source = source.Trim();
        if (source.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            var comma = source.IndexOf(',');
            if (comma <= 5) throw new InvalidOperationException("The image data is invalid.");
            var metadata = source[..comma];
            var payload = source[(comma + 1)..];
            if (metadata.Contains(";base64", StringComparison.OrdinalIgnoreCase))
                return Convert.FromBase64String(new string(Uri.UnescapeDataString(payload).Where(character => !char.IsWhiteSpace(character)).ToArray()));
            return Encoding.UTF8.GetBytes(Uri.UnescapeDataString(payload));
        }
        if (Uri.TryCreate(source, UriKind.Absolute, out var uri))
        {
            if (uri.Scheme is "http" or "https") throw new InvalidOperationException("Remote image URLs are not opened by PocketAgent.");
            if (uri.Scheme == Uri.UriSchemeFile) source = uri.LocalPath;
        }
        source = Uri.UnescapeDataString(source);
        var full = Path.GetFullPath(Path.IsPathRooted(source) ? source : Path.Combine(workingDirectory, source));
        var root = Path.GetFullPath(workingDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Images must be inside the conversation working directory.");
        var info = new FileInfo(full);
        if (!info.Exists) throw new FileNotFoundException("The image is no longer available on the desktop.");
        if (info.Length > 16 * 1024 * 1024) throw new InvalidOperationException("That image is too large to view.");
        return File.ReadAllBytes(full);
    }

    private static BitmapImage DecodeImage(byte[] bytes) => DecodeBitmap(bytes, 0);

    private static BitmapImage DecodeBitmap(byte[] bytes, int width)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.DecodePixelWidth = width;
        bitmap.StreamSource = stream;
        bitmap.EndInit();
        bitmap.Freeze();
        if (bitmap.PixelWidth <= 0 || bitmap.PixelHeight <= 0)
            throw new InvalidOperationException("The image has no displayable dimensions.");
        return bitmap;
    }

    private static string ImageExtension(DisplayImage image)
    {
        var mime = image.MimeType?.ToLowerInvariant();
        return mime switch
        {
            "image/jpeg" => ".jpg",
            "image/gif" => ".gif",
            "image/webp" => ".webp",
            "image/bmp" => ".bmp",
            "image/svg+xml" => ".svg",
            _ => SafeImageExtension(Path.GetExtension(image.Source).ToLowerInvariant())
        };
    }

    private static string SafeImageExtension(string extension) =>
        extension is ".jpg" or ".jpeg" or ".gif" or ".webp" or ".bmp" or ".svg" or ".tif" or ".tiff" or ".ico" ? extension : ".png";
    private void RenderWorkItem(DisplayItem item, Panel target)
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
        var details = TranscriptMarkdown.ReadOnlyText(string.IsNullOrWhiteSpace(item.Detail) ? "No additional details." : item.Detail, Brushes.LightGreen);
        details.Margin = new Thickness(12, 4, 12, 12);
        details.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
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
        target.Children.Add(card);
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
        Transcript.Children.Add(new Border { Child = panel, Tag = "transcript-notice", BorderBrush = Brushes.DarkKhaki, BorderThickness = new Thickness(1), Margin = new Thickness(0, 5, 0, 10) });
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
    private async void SaveCodexApiKey(object sender, RoutedEventArgs e) => await Act(() =>
    {
        if (relay is not null) throw new InvalidOperationException("Stop the server before replacing the Codex API key.");
        if (string.IsNullOrWhiteSpace(CodexApiKeySetting.Password)) throw new InvalidOperationException("Paste an OpenAI Platform API key first.");
        SaveConfiguration();
        ErrorDisplay.Text = "API key saved securely. Start the server, then run Check Codex connection.";
        return Task.CompletedTask;
    });

    private async void RemoveCodexApiKey(object sender, RoutedEventArgs e) => await Act(() =>
    {
        if (relay is not null) throw new InvalidOperationException("Stop the server before removing the Codex API key override.");
        CodexCredentialStore.Clear();
        settings.Host.CodexApiKeyOverride = false;
        settings.Save();
        UpdateCodexCredentialStatus();
        ErrorDisplay.Text = "Saved API-key override removed. Codex will use its environment or sign-in store when it starts.";
        return Task.CompletedTask;
    });

    private async void CheckProvider(object sender, RoutedEventArgs e) => await Act(async () =>
    {
        if (relay is null) throw new InvalidOperationException("Start the server first.");
        var diagnostics = relay.Services.GetRequiredService<ProviderDiagnostics>();
        var status = await diagnostics.Check();
        providerModel = status.ConfiguredModel ?? status.DefaultModel;
        var connection = relay.Services.GetRequiredService<CodexConnection>();
        var binary = connection.Executable;
        var fileVersion = binary is null ? null : TryFileVersion(binary.Path);
        ProviderDisplay.Text = $"{(status.Connected ? "Codex process connected" : "Codex process unavailable")}\n" +
            $"Authentication: {status.AuthenticationType ?? "not identified"}\n" +
            $"Credential: {(status.CredentialPresent ? status.CredentialSource : "not detected")} ({status.CredentialEnvironmentVariable})\n" +
            $"Credential fingerprint: {connection.CredentialInfo.Fingerprint ?? "unavailable"} (comparison only; key hidden)\n" +
            $"Provider: {status.Provider ?? "not identified"}\n" +
            $"Model: {status.ConfiguredModel ?? status.DefaultModel ?? "provider default"}\n" +
            $"Model catalog: {(status.ConfiguredModelInCatalog == false ? "configured model is absent" : status.Models.Length > 0 ? $"{status.Models.Length} model(s) available" : "unavailable")}\n" +
            "Inference access: not tested\n" +
            $"Executable: {binary?.Source ?? "not resolved"}\n{binary?.Path ?? ""}{(fileVersion is null ? "" : $"\nFile version: {fileVersion}")}" +
            (string.IsNullOrWhiteSpace(status.Error) ? "" : $"\nDiagnostic details: {status.Error}");
        ProviderResolutionPanel.Visibility = Visibility.Collapsed;
    });

    private async void VerifyProvider(object sender, RoutedEventArgs e) => await Act(async () =>
    {
        if (relay is null) throw new InvalidOperationException("Start the server first.");
        VerifyProviderButton.IsEnabled = false;
        try
        {
            var result = await relay.Services.GetRequiredService<ProviderDiagnostics>().VerifyInference();
            ProviderDisplay.Text = result.Verified
                ? $"Inference access verified\nModel: {result.Model ?? "provider default"}\nChecked: {result.CheckedAt.LocalDateTime:g}"
                : $"Inference access failed\nModel: {result.Model ?? "provider default"}\n{result.Message}";
            var projectFailure = result.ProviderStatus == 401 || result.Message.Contains("project", StringComparison.OrdinalIgnoreCase);
            ProviderResolutionPanel.Visibility = projectFailure ? Visibility.Visible : Visibility.Collapsed;
            ProviderResolutionText.Text = projectFailure
                ? "The credential was accepted far enough to identify its project, but that project was rejected for this model. Grant the project model access or replace the saved key, restart Codex, and run this test again."
                : "Update the credential or provider configuration, restart Codex, and run this test again.";
        }
        finally { VerifyProviderButton.IsEnabled = true; }
    });

    private async void RetryProviderUsage(object sender, RoutedEventArgs e) => await Act(async () =>
    {
        if (relay is null) throw new InvalidOperationException("Start the server first.");
        var usage = await relay.Services.GetRequiredService<ProviderUsage>().Check(true);
        UsageDisplay.Text = usage.Available
            ? $"5hr: {UsageText(usage.FiveHour, true)} | weekly: {UsageText(usage.Weekly, false)}"
            : usage.UnavailableReason ?? "Usage limits unavailable.";
        UsageDisplay.ToolTip = usage.UnavailableReason ?? "Usage limits are unavailable.";
    });

    private void OpenProviderProject(object sender, RoutedEventArgs e) => OpenExternal("https://platform.openai.com/settings/organization/projects");
    private void OpenProviderKeys(object sender, RoutedEventArgs e) => OpenExternal("https://platform.openai.com/api-keys");
    private static void OpenExternal(string address) => Process.Start(new ProcessStartInfo(address) { UseShellExecute = true });
    private static string? TryFileVersion(string path)
    {
        try { return FileVersionInfo.GetVersionInfo(path).FileVersion; }
        catch { return null; }
    }
    private void OpenGuide(object sender, RoutedEventArgs e)
    {
        var file = Path.Combine(AppContext.BaseDirectory, "documentation", "SETUP.md");
        if (File.Exists(file)) Process.Start(new ProcessStartInfo(file) { UseShellExecute = true });
        else ErrorDisplay.Text = "See documentation/SETUP.md in the repository or release package.";
    }
    private static string UsageText(UsageWindow? window, bool fiveHour)
    {
        if (window is null) return "unavailable";
        var reset = window.ResetsAt?.ToLocalTime();
        var resetText = reset is null ? "reset unavailable" : reset.Value.ToString(fiveHour ? "h:mm tt" : "M/d/yy", System.Globalization.CultureInfo.CurrentCulture);
        return $"{window.RemainingPercent}% {resetText}";
    }
    private static int TwelveHourClockHour(DateTime value) => value.Hour % 12 == 0 ? 12 : value.Hour % 12;
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
        var now = DateTime.Now;
        var date = new DatePicker { SelectedDate = now.Date, Margin = new Thickness(0, 4, 0, 10) };
        var hours = new ComboBox { ItemsSource = Enumerable.Range(1, 12).ToArray(), SelectedItem = TwelveHourClockHour(now), Width = 72 };
        var minutes = new ComboBox { ItemsSource = Enumerable.Range(0, 60).Select(value => value.ToString("D2")).ToArray(), SelectedItem = now.ToString("mm"), Width = 72 };
        var period = new ComboBox { ItemsSource = new[] { "AM", "PM" }, SelectedItem = now.ToString("tt", System.Globalization.CultureInfo.InvariantCulture), Width = 72 };
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
        SetQuick(now.AddMinutes(15));
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
