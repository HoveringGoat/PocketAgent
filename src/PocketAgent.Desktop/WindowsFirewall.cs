using System.ComponentModel;
using System.Diagnostics;

namespace PocketAgent.Desktop;

/// <summary>
/// Keeps the inbound listener rule independent of the versioned desktop executable path.
/// A port rule is intentionally created only after explicit user confirmation and elevation.
/// </summary>
public static class WindowsFirewall
{
    public static string RuleName(int port) => $"PocketAgent inbound TCP {port}";

    public static string[] AddRuleArguments(int port) =>
    [
        "advfirewall", "firewall", "add", "rule",
        $"name={RuleName(port)}", "dir=in", "action=allow",
        "protocol=TCP", $"localport={port}", "profile=any", "edge=no"
    ];

    public static bool IsConfigured(int port)
    {
        if (port is < 1 or > 65535) return false;
        var result = RunNetsh(
            ["advfirewall", "firewall", "show", "rule", $"name={RuleName(port)}"],
            elevated: false);
        return result.ExitCode == 0 && IsMatchingRuleOutput(port, result.Output);
    }

    internal static bool IsMatchingRuleOutput(int port, string output)
    {
        if (port is < 1 or > 65535 || output.Length == 0) return false;
        var values = output.Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Split(':', 2, StringSplitOptions.TrimEntries))
            .Where(parts => parts.Length == 2)
            .ToDictionary(parts => parts[0], parts => parts[1], StringComparer.OrdinalIgnoreCase);
        return values.TryGetValue("Enabled", out var enabled) && enabled.Equals("Yes", StringComparison.OrdinalIgnoreCase) &&
            values.TryGetValue("Direction", out var direction) && direction.Equals("In", StringComparison.OrdinalIgnoreCase) &&
            values.TryGetValue("Protocol", out var protocol) && protocol.Equals("TCP", StringComparison.OrdinalIgnoreCase) &&
            values.TryGetValue("LocalPort", out var localPort) && localPort.Split(',', StringSplitOptions.TrimEntries).Any(value => value == port.ToString(System.Globalization.CultureInfo.InvariantCulture)) &&
            values.TryGetValue("Action", out var action) && action.Equals("Allow", StringComparison.OrdinalIgnoreCase);
    }

    public static bool TryConfigure(int port)
    {
        if (port is < 1 or > 65535) return false;
        try
        {
            return RunNetsh(AddRuleArguments(port), elevated: true).ExitCode == 0;
        }
        catch (Win32Exception)
        {
            // UAC cancellation or an unavailable netsh should leave the server stopped.
            return false;
        }
    }

    private static NetshResult RunNetsh(IEnumerable<string> arguments, bool elevated)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "netsh.exe",
            UseShellExecute = elevated,
            Verb = elevated ? "runas" : "",
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardOutput = !elevated,
            RedirectStandardError = !elevated
        };
        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);
        using var process = Process.Start(startInfo);
        if (process is null) return new(-1, "");
        var output = elevated ? "" : process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        return new(process.ExitCode, output);
    }

    private sealed record NetshResult(int ExitCode, string Output);
}
