using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using PocketAgent;
using PocketAgent.Desktop;

internal static class ReasoningPickerChecks
{
    public static void Run()
    {
        var choiceType = typeof(MainWindow).GetNestedType("EffortChoice", BindingFlags.NonPublic)!;
        var factory = typeof(MainWindow).GetMethod("CreateModelEditor", BindingFlags.NonPublic | BindingFlags.Static)!;
        var models = new ModelOption[] {
            new("selected", "Selected model", true, "medium", ["medium", "xhigh", new string('W', 75)]),
            new("other", new string('M', 90), false, "low", ["low"])
        };
        var (dialog, model, combo) = ((Window, ComboBox, ComboBox))factory.Invoke(null, ["selected", "xhigh", models])!;
        var owner = new Window { Width = 800, Height = 580 };
        owner.Show();
        dialog.Owner = owner;
        dialog.Show();
        try
        {
            if (!ReferenceEquals(model.SelectedItem, models[0]) ||
                !Equals(choiceType.GetProperty("Label")!.GetValue(combo.SelectedItem), "Extra high"))
                throw new InvalidOperationException("The editor did not preselect the conversation's model and effort.");
            foreach (var size in new[] { new Size(800, 580), new Size(1150, 820) })
            {
                owner.Width = size.Width;
                owner.Height = size.Height;
                model.SelectedIndex = 1;
                dialog.UpdateLayout();
                if (combo.Items.Count != 1 || !Equals(choiceType.GetProperty("Label")!.GetValue(combo.SelectedItem), "Low"))
                    throw new InvalidOperationException("Changing the model must refresh the reasoning choices in the same dialog.");
                model.IsDropDownOpen = true;
                dialog.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                var modelPopup = Descendants(model).OfType<Popup>().Single();
                if (!Descendants(modelPopup.Child).OfType<TextBlock>().Any(item => item.Text == models[1].DisplayName))
                    throw new InvalidOperationException("The model dropdown did not render the long model name.");
                model.IsDropDownOpen = false;
                model.SelectedIndex = 0;
                var choices = combo.Items.Cast<object>().ToArray();
                for (var index = 0; index < choices.Length; index++)
                {
                    combo.SelectedIndex = index;
                    owner.UpdateLayout();
                    var label = (string)choiceType.GetProperty("Label")!.GetValue(choices[index])!;
                    var text = Descendants(combo).OfType<TextBlock>().SingleOrDefault(item => item.Text == label)
                        ?? throw new InvalidOperationException($"The collapsed reasoning picker did not render '{label}'.");
                    if (!ReferenceEquals(combo.SelectedItem, choices[index]) ||
                        !new Rect(combo.RenderSize).Contains(text.TransformToAncestor(combo).TransformBounds(new Rect(text.RenderSize))) ||
                        (label.Length > 60 && text.ActualHeight <= text.FontSize * 2))
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
        finally { dialog.Close(); owner.Close(); }
        Console.WriteLine("PASS model and reasoning editor preselection, dependent choices and dropdown layout at both window sizes.");
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
