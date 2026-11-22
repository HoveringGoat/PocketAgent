using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using PocketAgent.Desktop;

internal static class ReasoningPickerChecks
{
    public static void Run()
    {
        var choiceType = typeof(MainWindow).GetNestedType("EffortChoice", BindingFlags.NonPublic)!;
        object Choice(string? value, string label) => Activator.CreateInstance(choiceType, value, label)!;
        var choices = new[] { Choice(null, "Use model default"), Choice("xhigh", "Extra high"), Choice("long", new string('W', 75)) };
        var combo = new ComboBox { ItemsSource = choices, DisplayMemberPath = "Label", Width = 528, MinHeight = 38, Margin = new Thickness(16) };
        var owner = new Window { Width = 800, Height = 580, Content = new StackPanel { Children = { combo } } };
        owner.Show();
        try
        {
            foreach (var size in new[] { new Size(800, 580), new Size(1150, 820) })
            {
                owner.Width = size.Width;
                owner.Height = size.Height;
                for (var index = 0; index < choices.Length; index++)
                {
                    combo.SelectedIndex = index;
                    owner.UpdateLayout();
                    var label = (string)choiceType.GetProperty("Label")!.GetValue(choices[index])!;
                    var text = Descendants(combo).OfType<TextBlock>().SingleOrDefault(item => item.Text == label)
                        ?? throw new InvalidOperationException($"The collapsed reasoning picker did not render '{label}'.");
                    if (!ReferenceEquals(combo.SelectedItem, choices[index]) ||
                        !new Rect(combo.RenderSize).Contains(text.TransformToAncestor(combo).TransformBounds(new Rect(text.RenderSize))) ||
                        (index == 2 && text.ActualHeight <= text.FontSize * 2))
                        throw new InvalidOperationException($"The reasoning choice was clipped or changed at {size}.");
                }
                combo.IsDropDownOpen = true;
                owner.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                var popup = Descendants(combo).OfType<Popup>().Single();
                if (!Descendants(popup.Child).OfType<TextBlock>().Any(item => item.Text == "Extra high"))
                    throw new InvalidOperationException("The reasoning menu did not render the option label.");
                combo.IsDropDownOpen = false;
            }
        }
        finally { owner.Close(); }
        Console.WriteLine("PASS reasoning picker labels in the collapsed control and open dropdown at both window sizes.");
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }
}
