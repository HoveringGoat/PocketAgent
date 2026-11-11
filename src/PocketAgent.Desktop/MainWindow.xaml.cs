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
    private DesktopSettings settings = new();
    private WebApplication? relay;
    private readonly InternetConnector connector = new();
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly Dictionary<string, string> drafts = [];
    private ConversationSnapshot? conversation;
    private string? selected;
    private string rendered = "";
    private string sessionSignature = "";
    private bool refreshing, sending, settingDraft, changingList, closing, transitioning;
    private Conversations Conversations => relay!.Services.GetRequiredService<Conversations>();

    public MainWindow()
    {
        InitializeComponent();
        connector.AddressChanged += address => { if (!Dispatcher.HasShutdownStarted) Dispatcher.InvokeAsync(() => ConnectionAddress.Text = address); };
        Title = $"PocketAgent Server {BuildInfo.Version}";
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
        PublicOriginSetting.Text = settings.Host.PublicOrigin;
        TunnelExecutableSetting.Text = settings.TunnelExecutable;
        TunnelConfigurationSetting.Text = settings.TunnelConfiguration;
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
                PublicOrigin = PublicOriginSetting.Text.Trim(),
                PermissionMode = PermissionModeSetting.SelectedValue as string ?? "inherit",
                DataDirectory = DesktopSettings.DirectoryPath
            },
            TunnelExecutable = TunnelExecutableSetting.Text.Trim(),
            TunnelConfiguration = TunnelConfigurationSetting.Text.Trim()
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
        var addresses = NetworkInterface.GetAllNetworkInterfaces()
            .Where(network => network.OperationalStatus == OperationalStatus.Up && network.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .SelectMany(network => network.GetIPProperties().UnicastAddresses
                .Where(address => address.Address.AddressFamily == AddressFamily.InterNetwork && IsPrivateAddress(address.Address))
                .Select(address => $"{address.Address} ({network.Name})")).Distinct().ToArray();
        if (addresses.Length == 0) throw new InvalidOperationException("No private IPv4 address was found. Connect the desktop to Wi-Fi or Ethernet first.");
        var choices = new ComboBox { ItemsSource = addresses, SelectedIndex = 0, Margin = new Thickness(16) };
        var apply = new Button { Content = "Create certificate and use this address", Margin = new Thickness(16) };
        var panel = new StackPanel(); panel.Children.Add(new TextBlock { Text = "Choose the network shared with your phone. No firewall or router settings will be changed.", Margin = new Thickness(16) }); panel.Children.Add(choices); panel.Children.Add(apply);
        var dialog = new Window { Title = "Wi-Fi HTTPS setup", Owner = this, Width = 560, SizeToContent = SizeToContent.Height, Content = panel, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        apply.Click += (_, _) => dialog.DialogResult = true;
        if (dialog.ShowDialog() != true) return;
        var address = ((string)choices.SelectedItem).Split(' ')[0];
        var certificateDirectory = Path.Combine(DesktopSettings.DirectoryPath, "certificates", address);
        var certificate = Path.Combine(certificateDirectory, "server.pfx");
        if (!File.Exists(certificate)) await Task.Run(() => CertificateSetup.Create(certificateDirectory, address));
        AddressSetting.Text = address; CertificateSetting.Text = certificate; PublicOriginSetting.Text = "";
        SaveConfiguration();
        MessageBox.Show(this, $"Wi-Fi settings saved. Start the server, then enter https://{address}:{settings.Host.Port} in the Android app.\n\nTransfer ONLY pocketagent-root.cer from the certificate folder to your phone and install it as a CA certificate. Keep server.pfx private.\n\nAllow PocketAgent's port on the Windows private-network firewall if needed. No firewall or router changes were made.", "Wi-Fi setup", MessageBoxButton.OK, MessageBoxImage.Information);
        Process.Start(new ProcessStartInfo(certificateDirectory) { UseShellExecute = true });
    });
    private static bool IsPrivateAddress(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        return bytes[0] == 10 || bytes[0] == 172 && bytes[1] is >= 16 and <= 31 || bytes[0] == 192 && bytes[1] == 168;
    }
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
            MessageBox.Show(this, $"Stopping ends the Codex process and active work. {pending} pending scheduled message(s) will wait until the server runs again. Stop anyway?", "Stop server", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes;
    }
    private async Task Shutdown()
    {
        connector.Stop();
        var running = relay; relay = null;
        if (running is not null)
        {
            try { await running.StopAsync(); }
            finally { await running.DisposeAsync(); }
        }
        selected = null; conversation = null; rendered = ""; SessionList.ItemsSource = null;
        ConversationPanel.IsEnabled = false; Transcript.Children.Clear(); Composer.Text = "";
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
            timer.Stop(); await Shutdown(); connector.Dispose(); closing = true;
            _ = Dispatcher.BeginInvoke(Close);
        });
    }

    private sealed record SessionEntry(string Id, string Label);
    private async Task Refresh()
    {
        InternetStatus.Text = connector.IsRunning ? "Connector process running · verify your public address from the phone" : "Connector stopped";
        if (relay is null || refreshing) return;
        refreshing = true;
        await Act(async () =>
        {
            var running = relay;
            var sessions = await Conversations.List();
            if (relay != running) return;
            var usage = await running.Services.GetRequiredService<ProviderUsage>().Check();
            UsageDisplay.Text = $"5h: {UsageText(usage.FiveHour)} · Weekly: {UsageText(usage.Weekly)}";
            UsageDisplay.ToolTip = $"Five-hour reset: {usage.FiveHour?.ResetsAt?.LocalDateTime.ToString("g") ?? "unavailable"}\nWeekly reset: {usage.Weekly?.ResetsAt?.LocalDateTime.ToString("g") ?? "unavailable"}";
            var signature = JsonSerializer.Serialize(sessions);
            if (signature != sessionSignature)
            {
                changingList = true;
                SessionList.ItemsSource = sessions.Select(session => new SessionEntry(session.Id, $"{session.Title}\n{session.Status}")).ToArray();
                SessionList.SelectedItem = SessionList.Items.Cast<SessionEntry>().FirstOrDefault(item => item.Id == selected);
                changingList = false; sessionSignature = signature;
            }
            var code = running.Services.GetRequiredService<PairingCode>().Current;
            PairingDisplay.Text = code.Consumed ? "Code used" : code.ExpiresAt <= DateTimeOffset.UtcNow ? "Code expired" : code.Code;
            PairingExpiry.Text = $"Single use · expires {code.ExpiresAt.LocalDateTime:t}";
            if (Navigation.SelectedIndex == 1) RenderDevices();
            if (selected is null) return;
            var expected = selected;
            var snapshot = await Conversations.Get(expected);
            if (relay != running || selected != expected) return;
            conversation = snapshot;
            ConversationPanel.IsEnabled = true;
            ConversationTitle.Text = snapshot.Title;
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
        var created = await Conversations.Create(""); selected = created.Id; conversation = created; rendered = "";
        settingDraft = true; Composer.Text = ""; settingDraft = false;
        await Refresh(); Composer.Focus();
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
        string? lastMinute = null;
        foreach (var item in snapshot.Items)
        {
            if (item.Type is "userMessage" or "agentMessage" && item.Timestamp is { } timestamp)
            {
                var minute = timestamp.LocalDateTime.ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture);
                if (minute != lastMinute) Transcript.Children.Add(new TextBlock { Text = timestamp.LocalDateTime.ToString("t"), FontSize = 11, Foreground = Brushes.DarkSeaGreen, Margin = new Thickness(8, 5, 0, 5) });
                lastMinute = minute;
            }
            var content = new TextBlock { Text = item.Type is "userMessage" or "agentMessage" ? item.Text : $"{item.Type} · {item.Status}\n{item.Detail}", Margin = new Thickness(12), TextWrapping = TextWrapping.Wrap };
            var border = new Border { Child = content, Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(item.Type == "userMessage" ? "#2A3B2D" : "#19271F")), CornerRadius = new CornerRadius(8), Margin = new Thickness(0, 0, 0, 10) };
            Transcript.Children.Add(border);
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
    private void CopyAddress(object sender, RoutedEventArgs e) { if (ConnectionAddress.Text.Length > 0) Clipboard.SetText(ConnectionAddress.Text); }
    private void LocateAndroid(object sender, RoutedEventArgs e)
    {
        var apk = Path.Combine(AppContext.BaseDirectory, $"PocketAgent-{BuildInfo.Version}-android.apk");
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
    private void StartInternet(object sender, RoutedEventArgs e)
    {
        if (relay is null) { ErrorDisplay.Text = "Start the server first."; return; }
        if (MessageBox.Show(this, $"Expose the paired-device API at {settings.Host.ClientOrigin} using your named tunnel? Only paired devices can read or control conversations. Cloudflare terminates TLS and can process relayed data.", "Start internet access", MessageBoxButton.YesNo, MessageBoxImage.Information) != MessageBoxResult.Yes) return;
        _ = Act(() => { connector.Start(settings); return Task.CompletedTask; });
    }
    private void StopInternet(object sender, RoutedEventArgs e) => connector.Stop();
    private async void PrepareInternet(object sender, RoutedEventArgs e) => await Act(() =>
    {
        if (relay is not null) throw new InvalidOperationException("Stop the server before changing its listener.");
        AddressSetting.Text = "127.0.0.1"; CertificateSetting.Text = ""; PublicOriginSetting.Text = "";
        SaveConfiguration(); ErrorDisplay.Text = "Loopback listener prepared. Start the server, then choose temporary or named internet access.";
        return Task.CompletedTask;
    });
    private void StartTemporaryInternet(object sender, RoutedEventArgs e)
    {
        if (relay is null) { ErrorDisplay.Text = "Start the server first."; return; }
        if (MessageBox.Show(this, "Create a temporary public HTTPS address for this relay through Cloudflare? Pairing is still required. Cloudflare terminates TLS and can process relayed data. The address changes when restarted; use a named tunnel for a stable connection.", "Start temporary internet access", MessageBoxButton.YesNo, MessageBoxImage.Information) != MessageBoxResult.Yes) return;
        _ = Act(() => { connector.StartTemporary(settings); return Task.CompletedTask; });
    }
    private async void CreateTunnelConfiguration(object sender, RoutedEventArgs e) => await Act(() =>
    {
        if (relay is not null) throw new InvalidOperationException("Stop the server before configuring its public address.");
        var origin = PublicOriginSetting.Text.Trim();
        if (!Uri.TryCreate(origin, UriKind.Absolute, out var publicUri) || publicUri.Scheme != "https" || publicUri.HostNameType != UriHostNameType.Dns || publicUri.AbsolutePath != "/" || publicUri.UserInfo.Length > 0 || publicUri.Query.Length > 0 || publicUri.Fragment.Length > 0)
            throw new InvalidOperationException("Enter your stable public HTTPS hostname first.");
        var input = new TextBox { Margin = new Thickness(16) }; var create = new Button { Content = "Select credentials and create config", Margin = new Thickness(16) };
        var panel = new StackPanel(); panel.Children.Add(new TextBlock { Text = "Enter the UUID of a named tunnel already created in your Cloudflare account.", Margin = new Thickness(16) }); panel.Children.Add(input); panel.Children.Add(create);
        var dialog = new Window { Title = "Named tunnel", Owner = this, Content = panel, Width = 540, SizeToContent = SizeToContent.Height, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        create.Click += (_, _) => dialog.DialogResult = true;
        if (dialog.ShowDialog() != true) return Task.CompletedTask;
        if (!Guid.TryParse(input.Text, out var tunnelId)) throw new InvalidOperationException("Enter the tunnel UUID.");
        var picker = new OpenFileDialog { Title = "Select this tunnel's local credential JSON", Filter = "Tunnel credential files (*.json)|*.json" };
        if (picker.ShowDialog(this) != true) return Task.CompletedTask;
        AddressSetting.Text = "127.0.0.1"; CertificateSetting.Text = "";
        var configPath = Path.Combine(DesktopSettings.DirectoryPath, "cloudflared.yml");
        Directory.CreateDirectory(DesktopSettings.DirectoryPath);
        var config = $"tunnel: {tunnelId:D}\ncredentials-file: {JsonSerializer.Serialize(picker.FileName)}\ningress:\n  - hostname: {JsonSerializer.Serialize(publicUri.Host)}\n    service: http://127.0.0.1:{PortSetting.Text}\n    originRequest:\n      httpHostHeader: {JsonSerializer.Serialize(publicUri.Authority)}\n  - service: http_status:404\n";
        File.WriteAllText(configPath, config); TunnelConfigurationSetting.Text = configPath; SaveConfiguration();
        ErrorDisplay.Text = "Configuration saved. Route this hostname to the named tunnel in Cloudflare, then start the server and connector.";
        return Task.CompletedTask;
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
