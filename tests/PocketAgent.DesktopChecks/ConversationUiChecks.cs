using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
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
        Console.WriteLine("PASS desktop conversation layout, composer image paste and drop, scrolling, edit action, grouped activity, streamed Markdown and standalone image viewing.");
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
        if (!meta.Text.StartsWith("running - worked ", StringComparison.Ordinal) || !meta.Text.EndsWith($" - {snapshot.Model} High", StringComparison.Ordinal))
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
        CheckComposerAttachment(window, snapshot, variant);
        CheckComposerFileDrop(window, variant);
        CheckComposerArrowNavigation(window);
        var render = typeof(MainWindow).GetMethod("RenderTranscript", BindingFlags.NonPublic | BindingFlags.Instance)!;
        render.Invoke(window, [snapshot]);
        WaitFor(() => scroll.ScrollableHeight > 0 && NearBottom(scroll), "initial live edge", () => $"{scroll.VerticalOffset}/{scroll.ScrollableHeight}");
        var group = transcript.Children.OfType<Expander>().Single();
        if (group.IsExpanded || !Equals(group.Header, "Agent ran 10 commands"))
            throw new InvalidOperationException("Ten routine commands should form one collapsed activity group.");
        var userMessageSurface = Descendants(transcript).OfType<Border>().FirstOrDefault(border =>
            Descendants(border).OfType<TextBox>().Any(text => text.Text == "Earlier message 29"));
        if (userMessageSurface is not { Background: SolidColorBrush { Color: { A: 0 } } })
            throw new InvalidOperationException("User messages must keep the transcript background instead of rendering an opaque bubble.");
        if (!Descendants(transcript).OfType<Button>().Any(button => button.Content?.ToString()?.Contains("failed command") == true))
            throw new InvalidOperationException("A failed command must remain visible outside the routine activity group.");
        var header = (ToggleButton)group.Template.FindName("HeaderSite", group);
        var chevron = (System.Windows.Shapes.Path)header.Template.FindName("Chevron", header);
        var collapsedChevron = chevron.Data.ToString();
        var toggle = (IToggleProvider)new ToggleButtonAutomationPeer(header).GetPattern(PatternInterface.Toggle);
        toggle.Toggle();
        WaitFor(() => group.IsExpanded && chevron.Data.ToString() != collapsedChevron, "themed activity disclosure");
        window.UpdateLayout();
        var commandsPanel = (StackPanel)group.Content;
        if (commandsPanel.Children.Count != 10) throw new InvalidOperationException("Expanding the activity group must reveal all ten commands.");
        var first = Descendants(commandsPanel).OfType<Button>().First();
        first.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        if (!Descendants(commandsPanel).OfType<TextBox>().Any(label => label.IsVisible && label.IsReadOnly && label.Text.Contains("output 1")))
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
        var selectable = Descendants(transcript).OfType<RichTextBox>().FirstOrDefault(text =>
            new TextRange(text.Document.ContentStart, text.Document.ContentEnd).Text.Contains("Agent heading"));
        if (selectable is not { IsReadOnly: true })
            throw new InvalidOperationException("Agent responses must support native selection and copy.");
        selectable.SelectAll();
        if (!selectable.Selection.Text.Contains("Agent heading") || !selectable.Selection.Text.Contains("command output") ||
            !System.Windows.Input.ApplicationCommands.Copy.CanExecute(null, selectable))
            throw new InvalidOperationException("Native Copy must accept a selection spanning Markdown paragraphs and code.");
        var selectedText = selectable.Selection.Text;
        render.Invoke(window, [snapshot with { Items = [.. snapshot.Items[..^2], response with { Text = response.Text + "\n\nStreamed **update**.\n\nMore output." }, image] }]);
        if (!selectable.Selection.Text.StartsWith(selectedText.TrimEnd(), StringComparison.Ordinal))
            throw new InvalidOperationException("Streaming updates must preserve the user's existing response selection.");
        selectable.Selection.Select(selectable.Document.ContentStart, selectable.Document.ContentStart);
        var blocks = selectable.Document.Blocks.ToArray();
        static string Contents(TextElement element) => new TextRange(element.ContentStart, element.ContentEnd).Text.Trim();
        var markup = new Dictionary<string, bool>
        {
            ["heading"] = blocks.Any(block => block.FontSize > 15 && Contents(block) == "Agent heading"),
            ["emphasis"] = blocks.OfType<Paragraph>().Any(paragraph => paragraph.Inlines.OfType<Bold>().Any() && Contents(paragraph).Contains("Streamed update.")),
            ["link"] = blocks.OfType<Paragraph>().Any(paragraph => paragraph.Inlines.OfType<Hyperlink>().Any()),
            ["nested list"] = blocks.OfType<List>().Any(list => list.ListItems.Any(item => item.Blocks.OfType<List>().Any())),
            ["quote"] = blocks.OfType<Section>().Any(quote => Contents(quote) == "Quoted text"),
            ["table"] = blocks.OfType<Table>().Any(table => table.RowGroups[0].Rows[1].Cells.Count == 2 && Contents(table.RowGroups[0].Rows[1].Cells[1]) == "B"),
            ["code block"] = blocks.OfType<Paragraph>().Any(code => code.FontFamily.Source == "Consolas" && Contents(code) == "command output")
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
        foreach (var (width, height) in new[] { (window.MinWidth, window.MinHeight), (1150d, 820d) })
        {
            window.Width = width;
            window.Height = height;
            window.UpdateLayout();
            var viewport = (ScrollContentPresenter)scroll.Template.FindName("PART_ScrollContentPresenter", scroll);
            var activityOffset = scroll.VerticalOffset + group.TransformToAncestor(viewport).Transform(new Point()).Y;
            scroll.ScrollToVerticalOffset(activityOffset);
            WaitFor(() => Math.Abs(scroll.VerticalOffset - activityOffset) < 1 && jump.IsVisible, "activity scroll position");
            var headerBounds = header.TransformToAncestor(viewport).TransformBounds(new Rect(header.RenderSize));
            if (headerBounds.Top < -0.5 || headerBounds.Bottom > viewport.ActualHeight + 0.5 ||
                chevron.ActualWidth < 9 || chevron.ActualHeight < 9)
                throw new InvalidOperationException($"The activity disclosure clips at {width} × {height}.");
            Capture(window, Path.Combine(variant, $"grouped-activity-{width}-{height}.png"));
        }

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

    private static void CheckComposerAttachment(MainWindow window, ConversationSnapshot snapshot, string variant)
    {
        const BindingFlags fields = BindingFlags.Instance | BindingFlags.NonPublic;
        typeof(MainWindow).GetField("selected", fields)!.SetValue(window, snapshot.Id);
        typeof(MainWindow).GetField("conversation", fields)!.SetValue(window, snapshot);
        var bitmap = BitmapSource.Create(320, 180, 96, 96, PixelFormats.Gray8, null,
            Enumerable.Repeat((byte)190, 320 * 180).ToArray(), 320);
        typeof(MainWindow).GetMethod("AttachComposerImage", fields)!.Invoke(window, [snapshot.Id, bitmap]);
        var composer = (TextBox)window.FindName("Composer");
        var preview = (Image)window.FindName("ComposerPreview");
        var attachment = (DockPanel)window.FindName("ComposerAttachment");
        var remove = attachment.Children.OfType<Button>().Single();
        var send = (Button)window.FindName("SendButton");
        var schedule = (Button)window.FindName("ScheduleButton");
        if (!send.IsEnabled || schedule.IsEnabled || preview.Source is null || composer.Text.Length != 0)
            throw new InvalidOperationException("An image-only draft must enable Send and show a thumbnail.");

        composer.Text = string.Join("\n", Enumerable.Repeat(new string('W', 250), 10));
        foreach (var (width, height) in new[] { (window.MinWidth, window.MinHeight), (1150d, 820d) })
        {
            window.Width = width;
            window.Height = height;
            window.UpdateLayout();
            foreach (var control in new FrameworkElement[] { preview, remove, composer, send, schedule })
            {
                var bounds = control.TransformToAncestor(window).TransformBounds(new Rect(control.RenderSize));
                if (!control.IsVisible || !new Rect(window.RenderSize).Contains(bounds) || LayoutInformation.GetLayoutClip(control) is not null)
                    throw new InvalidOperationException($"A composer control clips at {width} × {height}.");
            }
            Capture(window, Path.Combine(variant, $"pasted-image-{width}-{height}.png"));
        }
        var caption = composer.Text;
        remove.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        if (attachment.Visibility != Visibility.Collapsed || composer.Text != caption || !schedule.IsEnabled)
            throw new InvalidOperationException("Removing the image must preserve the text and restore scheduling.");
        composer.Clear();
    }

    private static void CheckComposerFileDrop(MainWindow window, string variant)
    {
        var png = Path.Combine(variant, "drag-fixture.png");
        var jpeg = Path.Combine(variant, "drag-fixture.jpg");
        var invalid = Path.Combine(variant, "invalid-fixture.png");
        File.WriteAllBytes(png, Convert.FromBase64String(CreateImageFixture()));
        var jpgEncoder = new JpegBitmapEncoder();
        jpgEncoder.Frames.Add(BitmapFrame.Create(new Uri(png), BitmapCreateOptions.None, BitmapCacheOption.OnLoad));
        using (var output = File.Create(jpeg)) jpgEncoder.Save(output);
        File.WriteAllText(invalid, "Invalid image fixture");

        var composer = (TextBox)window.FindName("Composer");
        var target = (Grid)window.FindName("ComposerDropTarget");
        var hint = (Border)window.FindName("ComposerDropHint");
        var label = (TextBlock)window.FindName("ComposerDropHintLabel");
        var preview = (Image)window.FindName("ComposerPreview");
        var attachment = (DockPanel)window.FindName("ComposerAttachment");
        var remove = attachment.Children.OfType<Button>().Single();
        var send = (Button)window.FindName("SendButton");
        var errors = (TextBlock)window.FindName("ErrorDisplay");
        const string caption = "Keep this draft while attaching an image.";
        composer.Text = caption;

        var constructor = typeof(DragEventArgs).GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic).Single();
        DragDropEffects Drag(RoutedEvent action, params string[] paths)
        {
            // Exercise the preview route from the TextBox, where WPF has its own drop handling.
            var data = new DataObject(DataFormats.FileDrop, paths);
            var args = (DragEventArgs)constructor.Invoke([data, DragDropKeyStates.LeftMouseButton,
                DragDropEffects.Copy | DragDropEffects.Move, composer, new Point(10, 10)]);
            args.RoutedEvent = action;
            composer.RaiseEvent(args);
            return args.Effects;
        }

        foreach (var (width, height) in new[] { (window.MinWidth, window.MinHeight), (1150d, 820d) })
        {
            window.Width = width;
            window.Height = height;
            window.UpdateLayout();
            if (!target.AllowDrop || Drag(DragDrop.PreviewDragOverEvent, png) != DragDropEffects.Copy || !hint.IsVisible ||
                label.Text != "Drop image to attach" || hint.IsHitTestVisible)
                throw new InvalidOperationException("Dragging a PNG over the composer must show the copy drop target.");
            window.UpdateLayout();
            var hintBounds = hint.TransformToAncestor(target).TransformBounds(new Rect(hint.RenderSize));
            var labelBounds = label.TransformToAncestor(hint).TransformBounds(new Rect(label.RenderSize));
            if (!new Rect(target.RenderSize).Contains(hintBounds) || !new Rect(hint.RenderSize).Contains(labelBounds) ||
                LayoutInformation.GetLayoutClip(label) is not null)
                throw new InvalidOperationException($"The drop hint clips at {width} × {height}.");
            Capture(window, Path.Combine(variant, $"image-drag-hover-{width}-{height}.png"));
            Drag(DragDrop.PreviewDragLeaveEvent, png);
            if (hint.IsVisible) throw new InvalidOperationException("Leaving the composer must clear the drop hint.");

            var imagePath = width == window.MinWidth ? png : jpeg;
            if (Drag(DragDrop.PreviewDropEvent, imagePath) != DragDropEffects.Copy)
                throw new InvalidOperationException("Dropping a single image file must use the copy effect.");
            WaitFor(() => preview.Source is BitmapSource { PixelWidth: 320, PixelHeight: 180 }, "decoded dropped image");
            if (composer.Text != caption || !send.IsEnabled || hint.IsVisible)
                throw new InvalidOperationException("Dropping an image must preserve the caption and leave it ready to send.");
            window.UpdateLayout();
            Capture(window, Path.Combine(variant, $"image-dropped-{width}-{height}.png"));
            if (Drag(DragDrop.PreviewDragOverEvent, jpeg) != DragDropEffects.None ||
                label.Text != "Remove the current image before dropping another." || preview.Source is null)
                throw new InvalidOperationException("A second file must leave the current attachment in place.");
            window.UpdateLayout();
            var rejectionBounds = label.TransformToAncestor(hint).TransformBounds(new Rect(label.RenderSize));
            if (!new Rect(hint.RenderSize).Contains(rejectionBounds) || LayoutInformation.GetLayoutClip(label) is not null)
                throw new InvalidOperationException($"The rejected-drop hint clips at {width} × {height}.");
            Capture(window, Path.Combine(variant, $"image-drag-rejected-{width}-{height}.png"));
            Drag(DragDrop.PreviewDragLeaveEvent, jpeg);
            remove.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        }
        if (Drag(DragDrop.PreviewDragOverEvent, png, jpeg) != DragDropEffects.None ||
            Drag(DragDrop.PreviewDragOverEvent, Path.ChangeExtension(png, ".txt")) != DragDropEffects.None)
            throw new InvalidOperationException("The composer must reject multiple files and unsupported formats.");
        Drag(DragDrop.PreviewDropEvent, invalid);
        WaitFor(() => errors.Text.Contains("dropped image", StringComparison.OrdinalIgnoreCase), "unreadable drop feedback");
        if (composer.Text != caption || preview.Source is not null || !send.IsEnabled)
            throw new InvalidOperationException("The composer must preserve the caption after an unreadable drop.");
        errors.Text = "";
        composer.Clear();
    }

    private static void CheckComposerArrowNavigation(MainWindow window)
    {
        var composer = (TextBox)window.FindName("Composer");
        composer.Text = "first line\nmiddle line\nlast line";
        composer.SelectionLength = 0;
        composer.CaretIndex = "first line".Length;
        var up = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(window)!, 0, Key.Up)
        {
            RoutedEvent = Keyboard.PreviewKeyDownEvent
        };
        composer.RaiseEvent(up);
        if (!up.Handled || composer.CaretIndex != 0)
            throw new InvalidOperationException("Up on the first composer line must move the caret to its start.");

        composer.CaretIndex = composer.Text.Length - "last line".Length;
        var down = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(window)!, 0, Key.Down)
        {
            RoutedEvent = Keyboard.PreviewKeyDownEvent
        };
        composer.RaiseEvent(down);
        if (!down.Handled || composer.CaretIndex != composer.Text.Length)
            throw new InvalidOperationException("Down on the last composer line must move the caret to its end.");
        composer.Clear();
    }

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
