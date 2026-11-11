using System.Windows;
using System.Windows.Controls;

namespace PocketAgent.Desktop;

public static class AppDialogs
{
    public static MessageBoxResult Show(Window owner, string message, string title, MessageBoxButton buttons = MessageBoxButton.OK)
    {
        var result = MessageBoxResult.None;
        var content = new StackPanel { Margin = new Thickness(22) };
        content.Children.Add(new TextBlock { Text = message, Margin = new Thickness(0, 0, 0, 16) });
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        content.Children.Add(actions);
        var dialog = new Window
        {
            Title = title,
            Owner = owner,
            Width = 600,
            MaxHeight = SystemParameters.WorkArea.Height - 60,
            SizeToContent = SizeToContent.Height,
            Content = new ScrollViewer { Content = content },
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };
        foreach (var choice in buttons == MessageBoxButton.YesNo ? new[] { MessageBoxResult.Yes, MessageBoxResult.No } : new[] { MessageBoxResult.OK })
        {
            var button = new Button { Content = choice.ToString(), IsDefault = choice == MessageBoxResult.OK, IsCancel = choice == MessageBoxResult.No };
            button.Click += (_, _) => { result = choice; dialog.DialogResult = true; };
            actions.Children.Add(button);
        }
        dialog.ShowDialog();
        return result; // Closing a confirmation never means Yes.
    }
}
