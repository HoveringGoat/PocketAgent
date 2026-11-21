using System.Windows;
using System.Windows.Controls;

internal sealed class UpdaterWindow : Window
{
    private readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap };
    private readonly ProgressBar progress = new() { Height = 8, IsIndeterminate = true, Margin = new Thickness(0, 14, 0, 0) };
    private readonly TaskCompletionSource dismissed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly StackPanel content = new() { Margin = new Thickness(22) };
    private bool canClose;

    public UpdaterWindow(string version)
    {
        Title = "Updating PocketAgent";
        Width = 390;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        content.Children.Add(new TextBlock { Text = $"PocketAgent {version}", FontSize = 17, Margin = new Thickness(0, 0, 0, 10) });
        content.Children.Add(status);
        content.Children.Add(progress);
        Content = content;
        SetStatus("Waiting for PocketAgent to close…");
        Closing += (_, e) => e.Cancel = !canClose;
        Closed += (_, _) => dismissed.TrySetResult();
    }

    public void SetStatus(string message) => status.Text = message;

    public Task ShowFailure()
    {
        progress.Visibility = Visibility.Collapsed;
        var close = new Button { Content = "Close", Padding = new Thickness(12, 4, 12, 4), HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0), IsDefault = true };
        close.Click += (_, _) => Close();
        content.Children.Add(close);
        canClose = true;
        close.Focus();
        return dismissed.Task;
    }

    public void Finish()
    {
        canClose = true;
        Close();
    }
}
