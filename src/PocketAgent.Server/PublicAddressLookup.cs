using System.Net;
using System.Text.Json;

namespace PocketAgent;

public static class PublicAddressLookup
{
    public const string ServiceAddress = "https://api.ipify.org?format=json";

    public static async Task<string> Detect(CancellationToken cancellationToken = default)
    {
        // A separate, credential-free client: never reuse device/provider clients, cookies or proxy credentials.
        using var handler = new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false, UseProxy = false };
        using var client = new HttpClient(handler);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(8));
        return await Read(client, deadline.Token);
    }

    public static async Task<string> Read(HttpClient client, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await client.GetAsync(ServiceAddress, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (response.StatusCode != HttpStatusCode.OK || response.Content.Headers.ContentLength > 1024)
                throw new ApiException(502, "Public IP detection failed. Retry or enter your router's public IPv4 manually.");
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            var buffer = new byte[1025];
            var count = 0;
            while (count < buffer.Length)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(count), cancellationToken);
                if (read == 0) break;
                count += read;
            }
            if (count > 1024) throw new ApiException(502, "The public IP lookup returned an invalid response. Enter your router's public IPv4 manually.");
            using var document = JsonDocument.Parse(buffer.AsMemory(0, count));
            var value = document.RootElement.Text("ip");
            if (!IPAddress.TryParse(value, out var address) || address.ToString() != value || !HostSettings.IsPublicIPv4(address))
                throw new ApiException(502, "The lookup did not return a public IPv4 address. Enter your router's public IPv4 manually.");
            return value;
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or JsonException)
        { throw new ApiException(502, "Could not detect the public IP. Check internet access, retry, or enter your router's public IPv4 manually."); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { throw new ApiException(504, "Public IP detection timed out. Retry or enter the address manually."); }
    }
}
