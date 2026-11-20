using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using PocketAgent;
using PocketAgent.Desktop;

internal static class ConversationUiChecks
{
    public static void Run(MainWindow window, string root)
    {
        if (window.FindName("JumpToLatestButton") is not Button { Visibility: Visibility.Collapsed })
            throw new InvalidOperationException("Jump to latest must be present and hidden at the live edge.");
        if (window.FindName("EditConversationButton") is not Button edit ||
            AutomationProperties.GetName(edit) != "Edit conversation" ||
            window.FindName("PinButton") is not null || window.FindName("AccessButton") is not null)
            throw new InvalidOperationException("Conversation actions must use the Edit conversation button.");
        var settings = (DesktopSettings)typeof(MainWindow).GetField("settings", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
        settings.StartServerOnLaunch = false;
        settings.CheckUpdatesOnStartup = false;

        using var startup = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
        var startupValue = startup.GetValue("PocketAgent", null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        var startupKind = startupValue is null ? RegistryValueKind.Unknown : startup.GetValueKind("PocketAgent");
        try { CheckTranscript(window, root); }
        finally
        {
            window.Close();
            if (startupValue is not null) startup.SetValue("PocketAgent", startupValue, startupKind);
        }
        Console.WriteLine("PASS desktop conversation scrolling, status layout, edit action, grouped activity, streamed Markdown and standalone image viewing.");
    }

    private static void CheckTranscript(MainWindow window, string root)
    {
        var imageData = CreateImageFixture();
        using var tool = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            id = "view-image", type = "mcpToolCall", status = "completed", tool = "view_image",
            result = new { content = new[] { new { type = "image", mimeType = "image/png", data = imageData, caption = "Tool screenshot" } } }
        }));
        var image = (DisplayItem)typeof(Conversations).GetMethod("Render", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, [tool.RootElement])!;
        if (image.Images is not [{ AltText: "Tool screenshot" }]) throw new InvalidOperationException("MCP tool image content must survive provider projection.");
        var history = Enumerable.Range(0, 30).Select(i => new DisplayItem($"old-{i}", "userMessage", $"Earlier message {i}", "", "completed")).ToArray();
        var commands = Enumerable.Range(1, 10).Select(i => new DisplayItem($"command-{i}", "commandExecution", "", $"command {i}\noutput {i}", "completed")).ToArray();
        var response = new DisplayItem("response", "agentMessage", "# Agent heading\n\nA **strong** result and [a link](https://example.test).\n\n1. First step\n   - Nested step\n\n> Quoted text\n\n| Key | Value |\n| --- | --- |\n| A | B |\n\n~~~text\ncommand output\n~~~", "", "inProgress");
        var snapshot = new ConversationSnapshot("conversation-ui", "UI fixture", "running", "active-turn",
            [.. history, .. commands, new("failed", "commandExecution", "", "failed command", "failed"), response, image],
            [], null, "PocketAgent", root, "fixture-host", "inherit", null, null, null, 5000, new string('m', 115), false, "high");
        var variant = Path.Combine(root, ".local", "previews", "conversation-ui");
        Directory.CreateDirectory(variant);
        ((TabControl)window.FindName("Navigation")).SelectedIndex = 0;
        ((Grid)window.FindName("ConversationPanel")).IsEnabled = true;
        ((StackPanel)window.FindName("ConversationActions")).IsEnabled = true;
        ((ComboBox)window.FindName("UiScaleSetting")).SelectedValue = "large";
        var meta = (TextBlock)window.FindName("ConversationMeta");
        var format = typeof(MainWindow).GetMethod("FormatConversationMeta", BindingFlags.NonPublic | BindingFlags.Instance)!;
        meta.Text = (string)format.Invoke(window, [snapshot])!;
        if (!meta.Text.StartsWith("running - worked ", StringComparison.Ordinal) || !meta.Text.EndsWith($" - {snapshot.Model} high", StringComparison.Ordinal))
            throw new InvalidOperationException($"Desktop status must match the Android status format. Found {meta.Text}.");
        var stop = (Button)window.FindName("InterruptButton");
        stop.Visibility = Visibility.Visible;
        var scroll = (ScrollViewer)window.FindName("TranscriptScroll");
        var jump = (Button)window.FindName("JumpToLatestButton");
        var transcript = (StackPanel)window.FindName("Transcript");
        window.ShowActivated = false;
        window.Width = window.MinWidth;
        window.Height = window.MinHeight;
        window.Show();
        var render = typeof(MainWindow).GetMethod("RenderTranscript", BindingFlags.NonPublic | BindingFlags.Instance)!;
        render.Invoke(window, [snapshot]);
        WaitFor(() => scroll.ScrollableHeight > 0 && NearBottom(scroll), "initial live edge", () => $"{scroll.VerticalOffset}/{scroll.ScrollableHeight}");
        var group = transcript.Children.OfType<Expander>().Single();
        if (group.IsExpanded || !Equals(group.Header, "Agent ran 10 commands"))
            throw new InvalidOperationException("Ten routine commands should form one collapsed activity group.");
        if (!Descendants(transcript).OfType<Button>().Any(button => button.Content?.ToString()?.Contains("failed command") == true))
            throw new InvalidOperationException("A failed command must remain visible outside the routine activity group.");
        group.IsExpanded = true;
        window.UpdateLayout();
        var commandsPanel = (StackPanel)group.Content;
        if (commandsPanel.Children.Count != 10) throw new InvalidOperationException("Expanding the activity group must reveal all ten commands.");
        var first = Descendants(commandsPanel).OfType<Button>().First();
        first.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        if (!Descendants(commandsPanel).OfType<TextBlock>().Any(label => label.IsVisible && label.Text.Contains("output 1")))
            throw new InvalidOperationException("Each command in a group must independently reveal its output.");
        WaitFor(() => NearBottom(scroll), "live edge after expanding activity");
        scroll.ScrollToVerticalOffset(80);
        WaitFor(() => jump.IsVisible, "history jump control", () => $"{scroll.VerticalOffset}/{scroll.ScrollableHeight}, {jump.Visibility}");
        var existingThumbnail = transcript.Children.OfType<Button>().Single(button => AutomationProperties.GetName(button) == "Open image: Tool screenshot");
        var offset = scroll.VerticalOffset;
        render.Invoke(window, [snapshot with { Items = [.. snapshot.Items[..^2], response with { Text = response.Text + "\n\nStreamed **update**." }, image] }]);
        window.UpdateLayout();
        if (Math.Abs(scroll.VerticalOffset - offset) > 0.5 || !jump.IsVisible)
            throw new InvalidOperationException("Streaming should preserve the scroll position while reading history.");
        if (!transcript.Children.Contains(existingThumbnail) || !group.IsExpanded)
            throw new InvalidOperationException("Streaming text must preserve existing previews and expanded activity.");
        var labels = Descendants(transcript).OfType<TextBlock>().ToArray();
        var markup = new Dictionary<string, bool>
        {
            ["heading"] = labels.Any(label => label.Text == "Agent heading" && label.FontSize > 15),
            ["emphasis"] = labels.Any(label => label.Inlines.OfType<Bold>().Any() &&
                new TextRange(label.ContentStart, label.ContentEnd).Text.Contains("Streamed update.")),
            ["link"] = labels.Any(label => label.Inlines.OfType<Hyperlink>().Any()),
            ["nested list"] = labels.Any(label => label.Text == "Nested step"),
            ["quote"] = labels.Any(label => label.Text == "Quoted text"),
            ["table"] = labels.Any(label => label.Text.Trim() == "B" && label.Parent is StackPanel { Parent: Border { Parent: Grid } }),
            ["code block"] = Descendants(transcript).OfType<TextBox>().Any(code => code.IsReadOnly && code.Text.Contains("command output"))
        };
        if (markup.Values.Contains(false))
            throw new InvalidOperationException($"Missing rendered Markdown features: {string.Join(", ", markup.Where(pair => !pair.Value).Select(pair => pair.Key))}.");
        Capture(window, Path.Combine(variant, "reading-history.png"));
        jump.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        WaitFor(() => NearBottom(scroll) && !jump.IsVisible, "return to live edge", () => $"{scroll.VerticalOffset}/{scroll.ScrollableHeight}, {jump.Visibility}");
        var imageButton = transcript.Children.OfType<Button>().Single(button => AutomationProperties.GetName(button) == "Open image: Tool screenshot");
        WaitFor(() => Descendants(imageButton).OfType<Image>().Any(thumb => thumb.Source is not null), "decoded thumbnail");

        foreach (var (width, height) in new[] { (window.MinWidth, window.MinHeight), (1150d, 820d) })
        {
            window.Width = width;
            window.Height = height;
            window.UpdateLayout();
            var statusSurface = (Border)window.FindName("ConversationMetaSurface");
            var bounds = stop.TransformToAncestor(statusSurface).TransformBounds(new Rect(stop.RenderSize));
            if (!new Rect(statusSurface.RenderSize).Contains(bounds) || LayoutInformation.GetLayoutClip(stop) is not null ||
                !Descendants(stop).OfType<System.Windows.Shapes.Path>().Any(icon => icon.ActualWidth >= 15 && icon.ActualHeight >= 15))
                throw new InvalidOperationException($"Stop icon clips at {width} × {height} with Large scaling.");
            WaitFor(() => NearBottom(scroll), "live edge after resizing");
            var viewport = (ScrollContentPresenter)scroll.Template.FindName("PART_ScrollContentPresenter", scroll);
            var previewBounds = imageButton.TransformToAncestor(viewport).TransformBounds(new Rect(imageButton.RenderSize));
            if (previewBounds.Top < -0.5 || previewBounds.Bottom > viewport.ActualHeight + 0.5)
                throw new InvalidOperationException($"The latest image card is clipped at {width} × {height}.");
            Capture(window, Path.Combine(variant, $"conversation-{width}-{height}.png"));
        }
        group.IsExpanded = false;
        window.UpdateLayout();
        WaitFor(() => NearBottom(scroll), "live edge after collapsing activity");
        var activityOffset = scroll.VerticalOffset + group.TransformToAncestor(scroll).Transform(new Point()).Y;
        scroll.ScrollToVerticalOffset(activityOffset);
        WaitFor(() => jump.IsVisible, "activity scroll position");
        Capture(window, Path.Combine(variant, "grouped-activity.png"));

        var createEditor = typeof(MainWindow).GetMethod("CreateConversationEditor", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var (editor, name, pinned) = ((Window, TextBox, CheckBox))createEditor.Invoke(window, [snapshot with { Pinned = true }])!;
        editor.ShowActivated = false;
        editor.Show();
        editor.UpdateLayout();
        if (editor.Title != "Edit conversation" || name.Text != snapshot.Title || pinned.IsChecked != true ||
            !Descendants(editor).OfType<Button>().Any(button => Equals(button.Content, "Manage device access") && button.IsVisible))
            throw new InvalidOperationException("The editor must show rename, the current pin state, and device access together.");
        Capture(editor, Path.Combine(variant, "edit-conversation.png"));
        editor.Close();

        Window? viewer = null;
        var close = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        close.Tick += (_, _) =>
        {
            viewer = Application.Current.Windows.OfType<Window>().FirstOrDefault(candidate => candidate.Title == "Image");
            if (viewer is null) return;
            Capture(viewer, Path.Combine(variant, "image-viewer.png"));
            viewer.Close();
        };
        close.Start();
        try { imageButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); WaitFor(() => viewer is not null, "full image viewer"); }
        finally { close.Stop(); }
        if (imageButton.Tag is not TextBlock { Text: "Viewed image: Tool screenshot" })
            throw new InvalidOperationException("Opening the thumbnail must show the decoded image and retain viewed state.");
    }

    private static bool NearBottom(ScrollViewer scroll) => scroll.ScrollableHeight - scroll.VerticalOffset < 1;

    private static string CreateImageFixture()
    {
        const int width = 320, height = 180;
        var pixels = new byte[width * height * 3];
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
            {
                var index = (y * width + x) * 3;
                pixels[index] = (byte)(100 + y / 2);
                pixels[index + 1] = (byte)(75 + x / 3);
                pixels[index + 2] = (byte)(170 - y / 2);
            }
        var image = BitmapSource.Create(width, height, 96, 96, PixelFormats.Rgb24, null, pixels, width * 3);
        var png = new PngBitmapEncoder();
        png.Frames.Add(BitmapFrame.Create(image));
        using var bytes = new MemoryStream();
        png.Save(bytes);
        return Convert.ToBase64String(bytes.ToArray());
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject element)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(element); i++)
        {
            var child = VisualTreeHelper.GetChild(element, i);
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }

    private static void WaitFor(Func<bool> condition, string name, Func<string>? details = null)
    {
        var frame = new DispatcherFrame();
        var deadline = DateTime.UtcNow.AddSeconds(6);
        var tick = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(25) };
        tick.Tick += (_, _) => frame.Continue = !condition() && DateTime.UtcNow < deadline;
        tick.Start();
        Dispatcher.PushFrame(frame);
        tick.Stop();
        if (!condition()) throw new InvalidOperationException($"Timed out waiting for {name}. {details?.Invoke()}");
    }

    private static void Capture(Window window, string path)
    {
        var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(window);
        var png = new PngBitmapEncoder();
        png.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(path);
        png.Save(file);
    }
}
