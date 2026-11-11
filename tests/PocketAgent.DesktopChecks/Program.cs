using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
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
        try
        {
            var window = new MainWindow();
            ((TabControl)window.FindName("Navigation")).SelectedIndex = 0;
            ((Grid)window.FindName("ConversationPanel")).IsEnabled = true;
            var transcript = (StackPanel)window.FindName("Transcript");
            var now = DateTimeOffset.Now;
            var snapshot = new ConversationSnapshot("readability", "Readability", "completed", null,
                [new("user", "userMessage", "User sample", "", "completed", now), new("agent", "agentMessage", "Assistant sample", "", "completed", now), new("tool", "commandExecution", "", "Tool sample", "completed", now)],
                [new("approval", "item/commandExecution/requestApproval", JsonSerializer.SerializeToElement(new { reason = "Approval reason" }), ["decline"], true)],
                "Error sample", "PocketAgent", root, "fixture", "inherit", null, null, null, null, null, false);
            window.Show();
            typeof(MainWindow).GetMethod("RenderTranscript", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, [snapshot]);
            window.UpdateLayout();
            var texts = Descendants(transcript).OfType<TextBlock>().ToArray();
            foreach (var sample in new[] { "User sample", "Assistant sample", "Approval reason", "Your input is needed" })
                ColorIs(texts.Single(text => text.Text == sample).Foreground, "#C1DAA6", sample);
            ColorIs(texts.Single(text => text.Text.Contains("Tool sample", StringComparison.Ordinal)).Foreground, "#C1DAA6", "Tool output");
            ColorIs(texts.Single(text => text.Text == "Error sample").Foreground, "#FA8072", "Error text");
            ColorIs(texts.Single(text => text.FontSize == 11).Foreground, "#8FBC8F", "Timestamp");
            ColorIs(Descendants(transcript).OfType<Button>().Single(button => Equals(button.Content, "Deny")).Foreground, "#C1DAA6", "Button text");
            foreach (var screen in new[] { 0, 1, 2 })
            {
                ((TabControl)window.FindName("Navigation")).SelectedIndex = screen;
                window.UpdateLayout();
                CheckDarkSurface((TabControl)window.FindName("Navigation"), screen);
                foreach (var label in Descendants(window).OfType<TextBlock>().Where(label => label.IsVisible && label.Text.Length > 0))
                    if (label.Foreground is SolidColorBrush brush && brush.Color.R < 80 && brush.Color.G < 80 && brush.Color.B < 80)
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
}
