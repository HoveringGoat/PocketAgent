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
    private readonly HashSet<string> loadingComposerImages = [];

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
            if (loadingComposerImages.Contains(id)) return;
            AttachComposerImage(id, bitmap);
        }
        catch (Exception exception) when (exception is ExternalException or IOException or InvalidOperationException or NotSupportedException or ArgumentException)
        {
            e.Handled = true;
            ErrorDisplay.Text = "The clipboard image could not be attached.";
        }
    }

    private void ComposerFileDragOver(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop, autoConvert: false)) return;
        var problem = ComposerFileDropProblem(e.Data, out _);
        bool canCopy = (e.AllowedEffects & DragDropEffects.Copy) != 0;
        e.Effects = problem is null && canCopy ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
        ComposerDropHintLabel.Text = problem ?? (canCopy ? "Drop image to attach" : "This image can’t be copied.");
        ComposerDropHint.BorderBrush = e.Effects == DragDropEffects.Copy ? Brushes.LightGreen : Brushes.Salmon;
        ComposerDropHint.Visibility = Visibility.Visible;
    }

    private void ComposerFileDragLeave(object sender, DragEventArgs e) =>
        ComposerDropHint.Visibility = Visibility.Collapsed;

    private async void ComposerFileDrop(object sender, DragEventArgs e)
    {
        ComposerDropHint.Visibility = Visibility.Collapsed;
        if (!e.Data.GetDataPresent(DataFormats.FileDrop, autoConvert: false)) return;
        e.Handled = true;
        e.Effects = DragDropEffects.None;
        var problem = ComposerFileDropProblem(e.Data, out var path);
        if (problem is not null)
        {
            ErrorDisplay.Text = problem;
            return;
        }
        if ((e.AllowedEffects & DragDropEffects.Copy) == 0) return;
        e.Effects = DragDropEffects.Copy;
        await AttachDroppedImage(selected!, path!);
    }

    private string? ComposerFileDropProblem(IDataObject data, out string? path)
    {
        path = null;
        if (Composer.IsReadOnly || selected is not { } id || conversation is not { Archived: false } current ||
            current.Id != id || current.Status == "disconnected")
            return "Open a writable conversation to attach an image.";
        if (loadingComposerImages.Contains(id)) return "An image is still loading.";
        if (composerImages.ContainsKey(id)) return "Remove the current image before dropping another.";
        if (data.GetData(DataFormats.FileDrop, autoConvert: false) is not string[] { Length: 1 } paths)
            return "Drop one PNG or JPEG image at a time.";
        path = paths[0];
        return Path.GetExtension(path).ToLowerInvariant() is ".png" or ".jpg" or ".jpeg"
            ? null : "Drop a PNG or JPEG image file.";
    }

    private async Task AttachDroppedImage(string id, string path)
    {
        loadingComposerImages.Add(id);
        RefreshComposerActions();
        try
        {
            var image = await Task.Run(() => ReadDroppedImage(path));
            if (closing) return;
            composerImages[id] = image;
            if (selected == id) ErrorDisplay.Text = "";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or NotSupportedException or ArgumentException or FileFormatException or ExternalException)
        {
            if (selected == id) ErrorDisplay.Text = exception is InvalidDataException
                ? exception.Message : "The dropped image could not be opened.";
        }
        finally
        {
            loadingComposerImages.Remove(id);
            if (selected == id) RefreshComposerAttachment();
        }
    }

    private static ComposerImage ReadDroppedImage(string path)
    {
        using var stream = File.OpenRead(path);
        if (stream.Length > 24 * 1024 * 1024)
            throw new InvalidDataException("The image file must be 24 MiB or smaller.");
        var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.OnDemand);
        if (decoder is not (PngBitmapDecoder or JpegBitmapDecoder))
            throw new InvalidDataException("The dropped file isn’t a PNG or JPEG image.");
        var frame = decoder.Frames[0];
        var (width, height) = (frame.PixelWidth, frame.PixelHeight);
        if ((long)width * height > 64_000_000)
            throw new InvalidDataException("The image is too large to open.");
        stream.Position = 0;
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.StreamSource = stream;
        if (width >= height && width > MessageImage.MaxDimension)
            bitmap.DecodePixelWidth = MessageImage.MaxDimension;
        else if (height > MessageImage.MaxDimension)
            bitmap.DecodePixelHeight = MessageImage.MaxDimension;
        bitmap.EndInit();
        var image = EncodeComposerImage(bitmap);
        image.Preview.Freeze();
        return image;
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
            !loadingComposerImages.Contains(current.Id) &&
            current.Status is not ("starting" or "uncertain" or "disconnected");
        var hasText = !string.IsNullOrWhiteSpace(Composer.Text);
        SendButton.IsEnabled = canSubmit && (hasText || image is not null);
        ScheduleButton.IsEnabled = canSubmit && hasText && image is null;
        ScheduleButton.ToolTip = image is null ? "Schedule message" : "Remove the image to schedule this message";
    }
}
