using System.Text.Json;

namespace PocketAgent;

public static class CodexPermissions
{
    public static string Label(string mode) => mode switch
    {
        "full-access" => "Full access",
        "ask" => "Ask for approval",
        _ => "Use Codex settings"
    };

    public static Dictionary<string, object?> Apply(HostSettings settings, object parameters, bool turn = false, string? workingDirectory = null)
    {
        var result = JsonSerializer.SerializeToElement(parameters).EnumerateObject()
            .ToDictionary(property => property.Name, property => (object?)property.Value.Clone());
        // Only desktop configuration supplies the policy. Remote request bodies never select it.
        if (settings.PermissionMode == "inherit") return result;
        var fullAccess = settings.PermissionMode == "full-access";
        result["approvalPolicy"] = fullAccess ? "never" : "on-request";
        if (!turn) result["sandbox"] = fullAccess ? "danger-full-access" : "workspace-write";
        else result["sandboxPolicy"] = fullAccess
            ? (object)new { type = "dangerFullAccess" }
            : new { type = "workspaceWrite", writableRoots = new[] { workingDirectory ?? settings.RepositoryPath }, networkAccess = false };
        return result;
    }
}
