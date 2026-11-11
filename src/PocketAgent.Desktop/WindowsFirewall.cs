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
        return RunNetsh(
            ["advfirewall", "firewall", "show", "rule", $"name={RuleName(port)}"],
            elevated: false) == 0;
    }

    public static bool TryConfigure(int port)
    {
        if (port is < 1 or > 65535) return false;
        try
        {
            return RunNetsh(AddRuleArguments(port), elevated: true) == 0;
        }
        catch (Win32Exception)
        {
            // UAC cancellation or an unavailable netsh should leave the server stopped.
            return false;
        }
    }

    private static int RunNetsh(IEnumerable<string> arguments, bool elevated)
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
        if (process is null) return -1;
        process.WaitForExit();
        return process.ExitCode;
    }
}
