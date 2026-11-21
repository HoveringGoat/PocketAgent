using System.Reflection;
using System.Text.Json;
using PocketAgent.Desktop;

internal static class CodexUpdateChecks
{
    public static void Run()
    {
        var parser = typeof(CodexExtensionUpdateService).GetMethod("ReadLatestVersion", BindingFlags.Static | BindingFlags.NonPublic)!;
        Version Read(string payload)
        {
            using var response = JsonDocument.Parse(payload);
            return (Version)parser.Invoke(null, [response.RootElement, "win32-x64"])!;
        }

        foreach (var (payload, field) in new[]
        {
            ("""{"results":[{"extensions":[]}]}""", "extensions"),
            ("""{"results":[]}""", "results"),
            ("""{"results":[{"extensions":[{"versions":[]}]}]}""", "versions"),
            ("""{"results":[{"extensions":null}]}""", "extensions"),
            ("""{"results":[{}]}""", "extensions")
        })
        {
            try { Read(payload); }
            catch (TargetInvocationException exception) when (exception.InnerException is JsonException error && error.Message.Contains(field))
            { continue; }
            throw new InvalidOperationException($"Expected a diagnostic for Marketplace field '{field}'.");
        }

        var stable = Read("""
            {"results":[{"extensions":[{"versions":[
                {"version":"9.0.0","targetPlatform":"win32-x64","properties":[{"key":"Microsoft.VisualStudio.Code.PreRelease","value":"true"}]},
                {"version":"8.0.0","targetPlatform":"win32-arm64"},
                {"version":"bad","targetPlatform":"win32-x64"},
                {"version":"1.0.0","targetPlatform":"win32-x64"},
                {"version":"2.0.0","targetPlatform":"win32-x64"}
            ]}]}]}
            """);
        if (stable != new Version(2, 0, 0)) throw new InvalidOperationException("Expected the newest compatible stable version.");
        Console.WriteLine("PASS Codex Marketplace empty and malformed responses, stable channel, and Windows architecture selection.");
    }
}
