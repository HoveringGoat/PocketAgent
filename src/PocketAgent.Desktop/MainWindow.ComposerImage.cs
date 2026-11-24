using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PocketAgent.Desktop;

public partial class MainWindow
{
    private sealed record ComposerImage(byte[] Png, BitmapSource Preview);
    private readonly Dictionary<string, ComposerImage> composerImages = [];

    private ComposerImage? SelectedComposerImage => selected is null ? null : composerImages.GetValueOrDefault(selected);

    private void CanPasteComposerImage(object sender, CanExecuteRoutedEventArgs e)
    {
        if (e.Command != ApplicationCommands.Paste || Composer.IsReadOnly || conversation?.Id != selected || selected is null) return;
        try
        {
            if (!Clipboard.ContainsImage()) return;
            e.CanExecute = true;
            e.Handled = true;
        }
        catch (ExternalException) { /* The clipboard may be temporarily occupied. */ }
    }

    private void PasteComposerImage(object sender, ExecutedRoutedEventArgs e)
    {
        if (e.Command != ApplicationCommands.Paste || Composer.IsReadOnly || conversation?.Id != selected || selected is not { } id) return;
        try
        {
            if (Clipboard.GetImage() is not { } bitmap) return;
            e.Handled = true;
            if (composerImages.ContainsKey(id))
            {
                ErrorDisplay.Text = "Remove the current image before pasting another.";
                return;
            }
            AttachComposerImage(id, bitmap);
        }
        catch (Exception exception) when (exception is ExternalException or IOException or InvalidOperationException or NotSupportedException or ArgumentException)
        {
            e.Handled = true;
            ErrorDisplay.Text = "The clipboard image could not be attached.";
        }
    }

    private void AttachComposerImage(string id, BitmapSource bitmap)
    {
        composerImages[id] = EncodeComposerImage(bitmap);
        ErrorDisplay.Text = "";
        RefreshComposerAttachment();
    }

    private static ComposerImage EncodeComposerImage(BitmapSource input)
    {
        var scale = Math.Min(1, (double)MessageImage.MaxDimension / Math.Max(input.PixelWidth, input.PixelHeight));
        while (true)
        {
            var bitmap = scale == 1 ? input : new TransformedBitmap(input, new ScaleTransform(scale, scale));
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var output = new MemoryStream();
            encoder.Save(output);
            if (output.Length <= MessageImage.MaxBytes) return new(output.ToArray(), bitmap);
            scale *= 0.75;
        }
    }

    private void RemoveComposerImage(object sender, RoutedEventArgs e)
    {
        if (selected is not null) composerImages.Remove(selected);
        RefreshComposerAttachment();
        Composer.Focus();
    }

    private void RefreshComposerAttachment()
    {
        var image = SelectedComposerImage;
        ComposerPreview.Source = image?.Preview;
        ComposerAttachment.Visibility = image is null ? Visibility.Collapsed : Visibility.Visible;
        RefreshComposerActions();
    }

    private void RefreshComposerActions()
    {
        if (SendButton is null || ScheduleButton is null) return;
        var image = SelectedComposerImage;
        var canSubmit = !sending && conversation is { Archived: false } current && current.Id == selected &&
            current.Status is not ("starting" or "uncertain" or "disconnected");
        var hasText = !string.IsNullOrWhiteSpace(Composer.Text);
        SendButton.IsEnabled = canSubmit && (hasText || image is not null);
        ScheduleButton.IsEnabled = canSubmit && hasText && image is null;
        ScheduleButton.ToolTip = image is null ? "Schedule message" : "Remove the image to schedule this message";
    }
}
