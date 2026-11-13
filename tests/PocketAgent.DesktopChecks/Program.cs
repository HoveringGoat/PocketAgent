using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PocketAgent;
using PocketAgent.Desktop;

internal static class Program
{
    [STAThread]
    private static int Main()
    {
        var root = Directory.GetCurrentDirectory();
        Environment.SetEnvironmentVariable("POCKETAGENT_DESKTOP_DATA", Path.Combine(root, ".local", "desktop-color-" + Guid.NewGuid().ToString("N")));
        var application = new App();
        application.InitializeComponent();
        SynchronizationContext.SetSynchronizationContext(new System.Windows.Threading.DispatcherSynchronizationContext());
        try
        {
            var parser = typeof(UpdateService).GetMethod("TryDesktopVersion", BindingFlags.NonPublic | BindingFlags.Static)!;
            var parseArguments = new object?[] { "desktop-v0.3.2", null };
            if (!(bool)parser.Invoke(null, parseArguments)! || ((Version)parseArguments[1]!).ToString(3) != "0.3.2")
                throw new InvalidOperationException("The desktop updater must parse its platform release tag.");
            var window = new MainWindow();
            var settings = (DesktopSettings)typeof(MainWindow).GetField("settings", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window)!;
            settings.StartServerOnLaunch = false;
            settings.CheckUpdatesOnStartup = false;
            if (window.FindName("StartButton") is not null || window.FindName("StopButton") is not null || window.FindName("Menu") is not null)
                throw new InvalidOperationException("Desktop navigation must use top-level destinations and one server switch.");
            if (window.FindName("PinButton") is null)
                throw new InvalidOperationException("Desktop conversation actions must include pin/unpin.");
            if (Environment.GetCommandLineArgs().Contains("--interactive-image", StringComparer.OrdinalIgnoreCase))
                return RunInteractiveImagePreview(window, root);
            if (Environment.GetCommandLineArgs().Contains("--interactive-pin", StringComparer.OrdinalIgnoreCase))
                return RunInteractivePinPreview(window, root);
            foreach (var name in new[] { "RenameButton", "ModelButton", "PinButton", "ArchiveButton", "ArchivedButton" })
                if (((Button)window.FindName(name)!).ContentTemplate is null)
                    throw new InvalidOperationException($"Desktop conversation action {name} must use a vector icon template.");
            var serverToggle = (System.Windows.Controls.Primitives.ToggleButton)window.FindName("ServerToggle")!;
            if (serverToggle.IsChecked == true || !Equals(serverToggle.Content, "OFF"))
                throw new InvalidOperationException("The server switch must start in the OFF state.");
            var setServerToggle = typeof(MainWindow).GetMethod("SetServerToggle", BindingFlags.NonPublic | BindingFlags.Instance)!;
            setServerToggle.Invoke(window, [true, null, true]);
            if (serverToggle.IsChecked != true || !Equals(serverToggle.Content, "ON"))
                throw new InvalidOperationException("The server switch must show ON when enabled.");
            setServerToggle.Invoke(window, [false, "Starting...", false]);
            if (serverToggle.IsChecked == true || serverToggle.IsEnabled || !Equals(serverToggle.Content, "Starting..."))
                throw new InvalidOperationException("The server switch must show a disabled transition state while starting.");
            setServerToggle.Invoke(window, [false, null, true]);
            if (serverToggle.IsChecked == true || !Equals(serverToggle.Content, "OFF"))
                throw new InvalidOperationException("The server switch must show OFF when disabled.");
            var hourMethod = typeof(MainWindow).GetMethod("TwelveHourClockHour", BindingFlags.NonPublic | BindingFlags.Static)!;
            if ((int)hourMethod.Invoke(null, [new DateTime(2026, 1, 1, 0, 0, 0)])! != 12 ||
                (int)hourMethod.Invoke(null, [new DateTime(2026, 1, 1, 13, 0, 0)])! != 1)
                throw new InvalidOperationException("Schedule dialog must initialize a valid twelve-hour clock value.");
            var firewallRule = WindowsFirewall.AddRuleArguments(5147);
            if (WindowsFirewall.RuleName(5147) != "PocketAgent inbound TCP 5147" || !firewallRule.Contains("localport=5147") || firewallRule.Contains(Environment.ProcessPath ?? ""))
                throw new InvalidOperationException("Firewall setup must use a stable port rule rather than the versioned executable path.");
            var fixtureDirectory = Path.Combine(root, ".local", "qr-fixture"); Directory.CreateDirectory(fixtureDirectory);
            var fixturePayload = "pocketagent://pair?v=1&origin=https%3A%2F%2F127.0.0.1%3A5147&spki=" + new string('A', 64) + "&ticket=" + new string('B', 64);
            using (var data = QRCoder.QRCodeGenerator.GenerateQrCode(fixturePayload, QRCoder.QRCodeGenerator.ECCLevel.M))
            using (var qr = new QRCoder.PngByteQRCode(data)) File.WriteAllBytes(Path.Combine(fixtureDirectory, "desktop-qr.png"), qr.GetGraphic(6));
            File.WriteAllText(Path.Combine(fixtureDirectory, "desktop-qr.txt"), fixturePayload);
            var previewImageBytes = File.ReadAllBytes(Path.Combine(fixtureDirectory, "desktop-qr.png"));
            var imageBytes = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");
            var imagePath = Path.Combine(fixtureDirectory, "fixture-image.png");
            File.WriteAllBytes(imagePath, imageBytes);
            var spacedImagePath = Path.Combine(fixtureDirectory, "fixture image.png");
            File.WriteAllBytes(spacedImagePath, imageBytes);
            var imageReader = typeof(MainWindow).GetMethod("ReadImageBytes", BindingFlags.NonPublic | BindingFlags.Static)!;
            var imageDecoder = typeof(MainWindow).GetMethod("DecodeImage", BindingFlags.NonPublic | BindingFlags.Static)!;
            foreach (var source in new[] { "fixture-image.png", "fixture image.png", new Uri(imagePath).AbsoluteUri, $"data:image/png;base64,{Convert.ToBase64String(imageBytes)}" })
            {
                var decoded = (byte[])imageReader.Invoke(null, [source, fixtureDirectory])!;
                if (!decoded.SequenceEqual(imageBytes)) throw new InvalidOperationException($"Desktop image loading failed for {source}.");
                var bitmap = (BitmapImage)imageDecoder.Invoke(null, [decoded])!;
                if (bitmap.PixelWidth != 1 || bitmap.PixelHeight != 1)
                    throw new InvalidOperationException($"Desktop image rendering failed for {source}.");
            }
            var encodedTextImage = (byte[])imageReader.Invoke(null, ["data:image/svg+xml,%3Csvg%20xmlns%3D%27http%3A%2F%2Fwww.w3.org%2F2000%2Fsvg%27%2F%3E", fixtureDirectory])!;
            if (!System.Text.Encoding.UTF8.GetString(encodedTextImage).Contains("<svg", StringComparison.Ordinal))
                throw new InvalidOperationException("Percent-encoded image data must be decoded before format handling.");
            var renderItem = typeof(Conversations).GetMethod("Render", BindingFlags.NonPublic | BindingFlags.Static)!;
            using (var structuredImage = JsonDocument.Parse("{\"id\":\"image-item\",\"type\":\"agentMessage\",\"status\":\"completed\",\"content\":[{\"type\":\"image\",\"image_url\":{\"url\":\"fixture-image.png\"},\"mimeType\":\"image/png\"}]}"))
            {
                var renderedImage = (DisplayItem)renderItem.Invoke(null, [structuredImage.RootElement])!;
                if (renderedImage.Images is not [{ Source: "fixture-image.png", MimeType: "image/png" }])
                    throw new InvalidOperationException("Structured image URLs must survive provider payload normalization.");
            }
            using (var nestedStructuredImage = JsonDocument.Parse("{\"id\":\"nested-image-item\",\"type\":\"image\",\"image_url\":{\"url\":{\"uri\":\"fixture-image.png\"}}}"))
            {
                var renderedImage = (DisplayItem)renderItem.Invoke(null, [nestedStructuredImage.RootElement])!;
                if (renderedImage.Images is not [{ Source: "fixture-image.png" }])
                    throw new InvalidOperationException("Nested structured image URLs must normalize to a viewable source.");
            }
            using (var markdownImage = JsonDocument.Parse("{\"id\":\"markdown-image\",\"type\":\"agentMessage\",\"text\":\"![fixture](fixture image.png)\"}"))
            {
                var renderedImage = (DisplayItem)renderItem.Invoke(null, [markdownImage.RootElement])!;
                if (renderedImage.Images is not [{ Source: "fixture image.png" }])
                    throw new InvalidOperationException("Markdown image paths containing spaces must remain viewable.");
            }
            ((TabControl)window.FindName("Navigation")).SelectedIndex = 0;
            ((Grid)window.FindName("ConversationPanel")).IsEnabled = true;
            var transcript = (StackPanel)window.FindName("Transcript");
            var sessions = (ListBox)window.FindName("SessionList");
            sessions.DisplayMemberPath = "";
            foreach (var title in new[] { "Release planning", "Image review", "Network setup", "Updater tests", "Documentation" }) sessions.Items.Add(title);
            sessions.SelectedIndex = 0;
            ((TextBlock)window.FindName("ServerNameDisplay")).Text = "WORK-PC";
            ((TextBlock)window.FindName("ConversationLocation")).Text = "C:\\Github\\PocketAgent";
            ((TextBlock)window.FindName("UsageDisplay")).Text = "5hr: 63% · weekly: 8%";
            var now = DateTimeOffset.Now;
            var snapshot = new ConversationSnapshot("readability", "Readability", "completed", null,
                [new("user", "userMessage", "User sample", "", "completed", now), new("agent", "agentMessage", "Assistant sample", "", "completed", now), new("tool", "commandExecution", "", "Tool sample", "completed", now)],
                [new("approval", "item/commandExecution/requestApproval", JsonSerializer.SerializeToElement(new { reason = "Approval reason" }), ["decline"], true)],
                "Error sample", "PocketAgent", root, "fixture", "inherit", null, null, null, null, null, false);
            typeof(MainWindow).GetField("conversation", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(window, snapshot);
            window.Show();
            var initialMeta = (TextBlock)window.FindName("ConversationMeta")!;
            if (string.IsNullOrWhiteSpace(initialMeta.Text))
                throw new InvalidOperationException("The conversation status bar must identify the idle/no-selection state.");
            typeof(MainWindow).GetMethod("RenderTranscript", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, [snapshot]);
            window.UpdateLayout();
            var previewPath = Path.Combine(fixtureDirectory, "desktop-qr.png");
            var imageSource = new Uri(previewPath).AbsoluteUri;
            var imageItem = new DisplayItem("image-item", "agentMessage", "Image output", "", "completed", now, null, [new DisplayImage("image", imageSource, "fixture image", "image/png")]);
            typeof(MainWindow).GetMethod("RenderTranscript", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, [snapshot with { Items = [imageItem] }]);
            window.UpdateLayout();
            var imageButton = Descendants(transcript).OfType<Button>().Single(button => button.Content?.ToString()?.StartsWith("View image:", StringComparison.Ordinal) == true);
            var closeImageTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
            closeImageTimer.Tick += (_, _) =>
            {
                var imageDialog = Application.Current.Windows.OfType<Window>().SingleOrDefault(candidate => candidate.Title == "Image");
                if (imageDialog is null) return;
                if (Environment.GetCommandLineArgs().Contains("--image-preview"))
                    CaptureWindow(imageDialog, Path.Combine(root, ".local", "previews", "desktop", "desktop-image-dialog.png"));
                closeImageTimer.Stop(); imageDialog.Close();
            };
            closeImageTimer.Start();
            var showImage = typeof(MainWindow).GetMethod("ShowImage", BindingFlags.NonPublic | BindingFlags.Instance)!;
            var showImageTask = (Task)showImage.Invoke(window, [imageItem, imageItem.Images![0], $"{imageItem.Id}:{imageSource}", imageButton, "fixture image"])!;
            var imageFrame = new System.Windows.Threading.DispatcherFrame();
            _ = showImageTask.ContinueWith(_ => window.Dispatcher.BeginInvoke(new Action(() => imageFrame.Continue = false)), TaskScheduler.Default);
            System.Windows.Threading.Dispatcher.PushFrame(imageFrame);
            if (!showImageTask.IsCompleted)
                throw new InvalidOperationException($"Desktop image viewing did not complete. Status={showImageTask.Status}");
            if (showImageTask.IsFaulted)
                throw new InvalidOperationException($"Desktop image viewing faulted: {showImageTask.Exception}");
            if (!Equals(imageButton.Content, "Viewed image: fixture image"))
                throw new InvalidOperationException($"Desktop image viewing must open a decoded image dialog and retain viewed state. Content={imageButton.Content}; error={(window.FindName("ErrorDisplay") as TextBlock)?.Text}");
            typeof(MainWindow).GetMethod("RenderTranscript", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, [snapshot]);
            window.UpdateLayout();
            var metaSurface = (Border)window.FindName("ConversationMetaSurface")!;
            if (metaSurface.ActualHeight > 48)
                throw new InvalidOperationException("The conversation context bar must stay compact.");
            var formatConversationMeta = typeof(MainWindow).GetMethod("FormatConversationMeta", BindingFlags.NonPublic | BindingFlags.Instance)!;
            var conversationMeta = (string)formatConversationMeta.Invoke(window, [snapshot])!;
            if (!conversationMeta.Contains("completed", StringComparison.Ordinal) || conversationMeta.Contains("model", StringComparison.OrdinalIgnoreCase) || conversationMeta.Contains("reasoning", StringComparison.OrdinalIgnoreCase) || conversationMeta.Contains("selected", StringComparison.OrdinalIgnoreCase) || conversationMeta.Contains("provider policy", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The conversation context must stay to status and elapsed work only.");
            var sendButton = (Button)window.FindName("SendButton")!;
            if (!Equals(sendButton.Content, "Send"))
                throw new InvalidOperationException("The send action label must remain Send.");
            var interruptButton = (Button)window.FindName("InterruptButton")!;
            if (interruptButton.ContentTemplate is null || interruptButton.Visibility != Visibility.Collapsed)
                throw new InvalidOperationException("The interruption action must use a hidden-when-idle vector icon.");
            sendButton.ApplyTemplate(); window.UpdateLayout();
            var sendContent = Descendants(sendButton).OfType<ContentPresenter>().Single();
            ColorIs(TextElement.GetForeground(sendContent), "#111B17", "Primary Send label");
            if (Environment.GetCommandLineArgs().Contains("--toggle-preview"))
                CaptureSwitch(serverToggle, root, "off");
            setServerToggle.Invoke(window, [true, null, true]);
            window.UpdateLayout();
            if (Environment.GetCommandLineArgs().Contains("--toggle-preview"))
                CaptureSwitch(serverToggle, root, "on");
            var texts = Descendants(transcript).OfType<TextBlock>().ToArray();
            foreach (var sample in new[] { "User sample", "Assistant sample", "Approval reason", "Your input is needed" })
                ColorIs(texts.Single(text => text.Text == sample).Foreground, "#C1DAA6", sample);
            ColorIs(texts.First(text => text.Text.Contains("Tool sample", StringComparison.Ordinal)).Foreground, "#C1DAA6", "Tool output");
            ColorIs(texts.Single(text => text.Text == "Error sample").Foreground, "#FA8072", "Error text");
            ColorIs(texts.Single(text => text.FontSize == 11).Foreground, "#8FBC8F", "Timestamp");
            ColorIs(Descendants(transcript).OfType<Button>().Single(button => Equals(button.Content, "Deny")).Foreground, "#C1DAA6", "Button text");
            var workCard = Descendants(transcript).OfType<Button>().First(button => button.Content?.ToString()?.Contains("commandExecution", StringComparison.Ordinal) == true);
            if (!workCard.Content!.ToString()!.StartsWith("[+]", StringComparison.Ordinal)) throw new InvalidOperationException("Work items must start collapsed.");
            workCard.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            typeof(MainWindow).GetMethod("RenderTranscript", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, [snapshot]);
            window.UpdateLayout();
            var expandedWorkCard = Descendants(transcript).OfType<Button>().First(button => button.Content?.ToString()?.Contains("commandExecution", StringComparison.Ordinal) == true);
            if (!expandedWorkCard.Content!.ToString()!.StartsWith("[-]", StringComparison.Ordinal)) throw new InvalidOperationException("Work item expansion must survive a transcript refresh.");
            foreach (var screen in new[] { 0, 1, 2 })
            {
                ((TabControl)window.FindName("Navigation")).SelectedIndex = screen;
                window.UpdateLayout();
                CheckDarkSurface((TabControl)window.FindName("Navigation"), screen);
                foreach (var label in Descendants(window).OfType<TextBlock>().Where(label => label.IsVisible && label.Text.Length > 0))
                    if (label.Text != "Send" && label.Foreground is SolidColorBrush brush && brush.Color.R < 80 && brush.Color.G < 80 && brush.Color.B < 80)
                        throw new InvalidOperationException($"Unreadable dark text on screen {screen}: {label.Text}");
            }
            var permissions = (ComboBox)window.FindName("PermissionModeSetting");
            permissions.IsDropDownOpen = true; window.UpdateLayout();
            foreach (ComboBoxItem choice in permissions.Items) ColorIs(choice.Foreground, "#C1DAA6", "Dropdown choice");
            permissions.IsDropDownOpen = false;
            var scrollbars = Descendants(window).OfType<System.Windows.Controls.Primitives.ScrollBar>().Where(bar => bar.IsVisible).ToArray();
            if (scrollbars.Length == 0) throw new InvalidOperationException("The settings regression must exercise a visible scrollbar.");
            foreach (var scrollbar in scrollbars) ColorIs(scrollbar.Background, "#18271F", "Scrollbar track");
            CheckConfirmation(window, dismiss: false);
            CheckConfirmation(window, dismiss: true);
            CheckLanDefault(window);
            if (window.FindName("TunnelExecutableSetting") is not null || window.FindName("PublicOriginSetting") is not null)
                throw new InvalidOperationException("Obsolete tunnel settings remain in the UI.");

            var largeItems = Enumerable.Range(0, 1_000)
                .Select(index => new DisplayItem($"item-{index}", "agentMessage", new string('x', 1_000), "", "completed", now))
                .ToArray();
            var largeSnapshot = snapshot with { Items = largeItems, Requests = [], Error = null };
            typeof(MainWindow).GetMethod("RenderTranscript", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, [largeSnapshot]);
            var renderedLarge = Descendants(transcript).OfType<TextBlock>().Count(text => text.Text.Contains(new string('x', 1_000), StringComparison.Ordinal));
            if (renderedLarge > 300) throw new InvalidOperationException($"Large transcript rendered {renderedLarge} full items instead of staying within the desktop rendering budget.");
            Console.WriteLine("PASS readable native screens/dropdown, dark scrollbar tracks and rendered surfaces, themed confirmation Yes/dismiss, removed legacy controls and bounded transcript.");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception.Message);
            return 1;
        }
        finally { application.Shutdown(); }
    }

    private static int RunInteractiveImagePreview(MainWindow window, string root)
    {
        var imagePath = Path.Combine(root, "deadspace.png");
        if (!File.Exists(imagePath)) throw new InvalidOperationException($"Preview image was not found: {imagePath}");
        var now = DateTimeOffset.Now;
        var snapshot = new ConversationSnapshot("interactive-image", "Interactive image preview", "completed", null,
            [new DisplayItem("image-preview", "agentMessage", "This is a file-backed image from the current desktop build.", "", "completed", now, null,
                [new DisplayImage("image-preview", imagePath, "deadspace.png", "image/png")])],
            [], null, "PocketAgent", root, Environment.MachineName, "fixture", "inherit", null, null, null, null, false);
        typeof(MainWindow).GetField("conversation", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(window, snapshot);
        ((Grid)window.FindName("ConversationPanel")!).IsEnabled = true;
        window.Show();
        typeof(MainWindow).GetMethod("RenderTranscript", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, [snapshot]);
        window.Activate();
        Console.WriteLine("Interactive image preview is open. Click 'View image: deadspace.png' and report what happens; close the window when done.");
        System.Windows.Threading.Dispatcher.Run();
        return 0;
    }

    private static int RunInteractivePinPreview(MainWindow window, string root)
    {
        var now = DateTimeOffset.Now;
        var snapshot = new ConversationSnapshot("interactive-pin", "Pin icon preview", "completed", null,
            [new DisplayItem("pin-preview", "agentMessage", "The pin button is shown in the conversation action row.", "", "completed", now)],
            [], null, "PocketAgent", root, Environment.MachineName, "fixture", "inherit", null, null, null, null, false);
        typeof(MainWindow).GetField("conversation", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(window, snapshot);
        ((Grid)window.FindName("ConversationPanel")!).IsEnabled = true;
        ((StackPanel)window.FindName("ConversationActions")!).IsEnabled = true;
        ((TextBlock)window.FindName("ServerNameDisplay")!).Text = Environment.MachineName;
        ((TextBlock)window.FindName("ConversationLocation")!).Text = root;
        ((TextBlock)window.FindName("UsageDisplay")!).Text = "Preview";
        ((TabControl)window.FindName("Navigation")!).SelectedIndex = 0;
        window.Show();
        typeof(MainWindow).GetMethod("RenderTranscript", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, [snapshot]);
        window.UpdateLayout();
        CaptureWindow(window, Path.Combine(root, ".local", "previews", "desktop", "pin-preview.png"));
        window.Activate();
        Console.WriteLine("Interactive pin preview is open. Inspect the pushpin icon in the action row; close the window when finished.");
        System.Windows.Threading.Dispatcher.Run();
        return 0;
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }

    private static void ColorIs(Brush brush, string expected, string label)
    {
        if (brush is not SolidColorBrush actual || actual.Color != (Color)ColorConverter.ConvertFromString(expected))
            throw new InvalidOperationException($"{label}: expected readable foreground {expected}, observed {brush}.");
    }

    private static void CaptureSwitch(FrameworkElement control, string root, string state)
    {
        // Let the real template's 140 ms slide finish before capturing its endpoint.
        var frame = new System.Windows.Threading.DispatcherFrame();
        var settle = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(180) };
        settle.Tick += (_, _) => { settle.Stop(); frame.Continue = false; };
        settle.Start();
        System.Windows.Threading.Dispatcher.PushFrame(frame);
        const int padding = 20;
        var width = control.ActualWidth + padding * 2;
        var height = control.ActualHeight + padding * 2;
        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen())
        {
            drawing.DrawRectangle(new SolidColorBrush(Color.FromRgb(17, 27, 23)), null, new Rect(0, 0, width, height));
            drawing.DrawRectangle(new VisualBrush(control), null, new Rect(padding, padding, control.ActualWidth, control.ActualHeight));
        }
        var image = new RenderTargetBitmap((int)(width * 2), (int)(height * 2), 192, 192, PixelFormats.Pbgra32);
        image.Render(visual);
        var directory = Path.Combine(root, ".local", "previews", "desktop");
        Directory.CreateDirectory(directory);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
        using var output = File.Create(Path.Combine(directory, $"server-switch-{state}.png"));
        encoder.Save(output);

        var window = Window.GetWindow(control);
        var full = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        full.Render(window);
        var fullEncoder = new PngBitmapEncoder(); fullEncoder.Frames.Add(BitmapFrame.Create(full));
        using var fullOutput = File.Create(Path.Combine(directory, $"desktop-switch-{state}.png"));
        fullEncoder.Save(fullOutput);
    }

    private static void CaptureWindow(Window window, string path)
    {
        window.UpdateLayout();
        var image = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        image.Render(window);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var output = File.Create(path);
        encoder.Save(output);
    }

    private static void CheckDarkSurface(FrameworkElement element, int screen)
    {
        var image = new RenderTargetBitmap((int)element.ActualWidth, (int)element.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        image.Render(element);
        var pixels = new byte[image.PixelWidth * image.PixelHeight * 4];
        image.CopyPixels(pixels, image.PixelWidth * 4, 0);
        var bright = 0;
        for (var index = 0; index < pixels.Length; index += 4)
            if (pixels[index + 3] > 240 && pixels[index] > 225 && pixels[index + 1] > 225 && pixels[index + 2] > 225) bright++;
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
        using (var output = File.Create(Path.Combine(Directory.GetCurrentDirectory(), ".local", $"desktop-theme-screen-{screen}.png"))) encoder.Save(output);
        if (bright > image.PixelWidth * image.PixelHeight / 20)
            throw new InvalidOperationException($"Screen {screen}: {bright * 100.0 / (image.PixelWidth * image.PixelHeight):F1}% of the rendered surface is harsh white/light gray.");
    }

    private static void CheckConfirmation(Window owner, bool dismiss)
    {
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(20) };
        timer.Tick += (_, _) =>
        {
            var dialog = Application.Current.Windows.OfType<Window>().SingleOrDefault(window => window.Title == "Theme confirmation check");
            if (dialog is null) return;
            timer.Stop(); dialog.UpdateLayout();
            ColorIs(dialog.Background, "#111B17", "Confirmation background");
            CheckDarkSurface((FrameworkElement)dialog.Content, dismiss ? 91 : 90);
            if (dismiss) dialog.Close();
            else Descendants(dialog).OfType<Button>().Single(button => Equals(button.Content, "Yes")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        };
        timer.Start();
        try
        {
            var result = AppDialogs.Show(owner, "Readable confirmation content", "Theme confirmation check", MessageBoxButton.YesNo);
            if (dismiss ? result == MessageBoxResult.Yes : result != MessageBoxResult.Yes)
                throw new InvalidOperationException("Themed confirmation changed explicit Yes/dismiss behavior.");
        }
        finally { timer.Stop(); }
    }

    private static void CheckLanDefault(MainWindow owner)
    {
        var preferred = System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
            .Where(network => network.OperationalStatus == System.Net.NetworkInformation.OperationalStatus.Up && network.GetIPProperties().GatewayAddresses.Any(gateway => gateway.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork && !gateway.Address.Equals(System.Net.IPAddress.Any)))
            .SelectMany(network => network.GetIPProperties().UnicastAddresses).Select(address => address.Address.ToString()).ToHashSet();
        if (preferred.Count == 0) return;
        string? failure = null;
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(20) };
        timer.Tick += (_, _) =>
        {
            var dialog = Application.Current.Windows.OfType<Window>().SingleOrDefault(window => window.Title == "Direct internet setup");
            if (dialog is null) return;
            timer.Stop(); dialog.UpdateLayout();
            var selection = Descendants(dialog).OfType<ComboBox>().First().SelectedItem.ToString()!.Split(' ')[0];
            if (!preferred.Contains(selection)) failure = "Direct setup defaulted to an adapter without an IPv4 gateway instead of the available LAN adapter.";
            dialog.Close();
        };
        timer.Start();
        typeof(MainWindow).GetMethod("SetupDirectInternet", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(owner, [owner, new RoutedEventArgs()]);
        timer.Stop();
        if (failure is not null) throw new InvalidOperationException(failure);
    }
}
