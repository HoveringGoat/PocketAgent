using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;

namespace PocketAgent.Desktop;

public partial class MainWindow
{
    private static string? RoutineActivity(DisplayItem item)
    {
        if (item.Images is { Length: > 0 } || item.Status is not ("" or "inProgress" or "running" or "completed"))
            return null;
        return item.Type switch
        {
            "commandExecution" or "fileChange" or "mcpToolCall" or "webSearch" => item.Type,
            _ => null
        };
    }

    private void RenderActivityGroup(DisplayItem[] items, string action)
    {
        var running = items.Any(item => item.Status is "inProgress" or "running");
        var count = items.Length;
        var name = action switch
        {
            "commandExecution" => running ? "Agent running commands" : count == 1 ? "Agent ran 1 command" : $"Agent ran {count} commands",
            "fileChange" => running ? "Agent editing files" : count == 1 ? "Agent made 1 file change" : $"Agent made {count} file changes",
            "mcpToolCall" => running ? "Agent using tools" : count == 1 ? "Agent called 1 tool" : $"Agent called {count} tools",
            _ => running ? "Agent searching the web" : count == 1 ? "Agent searched the web once" : $"Agent searched the web {count} times"
        };
        var key = $"group:{items[0].Id}";
        var details = new StackPanel { Margin = new Thickness(12, 3, 0, 0) };
        var group = new Expander
        {
            Header = running && count > 1 ? $"{name} ({count})" : name,
            Content = details, IsExpanded = expandedWorkItems.Contains(key),
            Margin = new Thickness(8, 3, 8, 5)
        };
        AutomationProperties.SetName(group, group.Header.ToString());
        if (group.IsExpanded)
            foreach (var item in items) RenderWorkItem(item, details);
        group.Expanded += (sender, e) =>
        {
            if (!ReferenceEquals(e.OriginalSource, sender)) return;
            expandedWorkItems.Add(key);
            if (details.Children.Count == 0)
                foreach (var item in items) RenderWorkItem(item, details);
        };
        group.Collapsed += (sender, e) =>
        {
            if (ReferenceEquals(e.OriginalSource, sender)) expandedWorkItems.Remove(key);
        };
        Transcript.Children.Add(group);
    }
}
