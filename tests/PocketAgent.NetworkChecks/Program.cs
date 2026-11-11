using System.Net;
using System.Text;
using PocketAgent;

static void Check(bool value, string message)
{
    if (!value) throw new InvalidOperationException(message);
}

using (var client = new HttpClient(new ReplyHandler(request =>
{
    Check(request.Method == HttpMethod.Get && request.RequestUri == new Uri("https://api.ipify.org/?format=json"), "Use only the fixed IPv4 HTTPS lookup endpoint.");
    Check(request.Content is null && !request.Headers.Any(), "Lookup must not add credentials, pairing information, chat data or custom headers.");
    return new(HttpStatusCode.OK) { Content = new StringContent("{\"ip\":\"203.0.113.10\"}") };
})))
    Check(await PublicAddressLookup.Read(client, CancellationToken.None) == "203.0.113.10", "The detected IPv4 must be returned without changing it.");
Console.WriteLine("PASS fixed, credential-free lookup request and valid IPv4 response");

foreach (var body in new[] { "not-json", "[]", "{\"ip\":123}", "{\"ip\":\"192.168.1.2\"}", "{\"ip\":\"100.64.0.1\"}", "{\"ip\":\"127.0.0.1\"}", "{\"ip\":\"169.254.1.2\"}", "{\"ip\":\"::1\"}", "{\"ip\":\"https://attacker.example\"}", new string('x', 2048) })
{
    using var client = new HttpClient(new ReplyHandler(_ => new(HttpStatusCode.OK) { Content = new StringContent(body) }));
    try { await PublicAddressLookup.Read(client, CancellationToken.None); throw new InvalidOperationException("An invalid lookup response was accepted."); }
    catch (ApiException exception) { Check(exception.Status == 502, "Invalid lookup responses must fail explicitly."); }
}
Console.WriteLine("PASS malformed, private, carrier NAT, loopback, IPv6 and oversized responses rejected");

using (var client = new HttpClient(new ReplyHandler(_ => new(HttpStatusCode.OK) { Content = new UnknownLengthContent() })))
{
    try { await PublicAddressLookup.Read(client, CancellationToken.None); throw new InvalidOperationException("An unbounded response was accepted."); }
    catch (ApiException exception) { Check(exception.Status == 502, "Unknown-length oversized responses must also be bounded."); }
}
Console.WriteLine("PASS unknown-length response is bounded");

foreach (var status in new[] { HttpStatusCode.Found, HttpStatusCode.ServiceUnavailable })
{
    using var client = new HttpClient(new ReplyHandler(_ => new(status) { Content = new StringContent("{\"ip\":\"203.0.113.10\"}") }));
    try { await PublicAddressLookup.Read(client, CancellationToken.None); throw new InvalidOperationException("A redirect or failed lookup was accepted."); }
    catch (ApiException exception) { Check(exception.Status == 502, "Redirects and service failures must not become usable addresses."); }
}
Console.WriteLine("PASS redirects and service failures rejected");

using (var cancellation = new CancellationTokenSource())
using (var client = new HttpClient(new ReplyHandler(_ => new(HttpStatusCode.OK))))
{
    cancellation.Cancel();
    try { await PublicAddressLookup.Read(client, cancellation.Token); throw new InvalidOperationException("Canceled detection completed."); }
    catch (OperationCanceledException) { }
}
Console.WriteLine("PASS closing/canceling detection cancels the request; no external service was contacted");
await AcmeChecks.Run();

sealed class ReplyHandler(Func<HttpRequestMessage, HttpResponseMessage> reply) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(reply(request));
    }
}

sealed class UnknownLengthContent : HttpContent
{
    protected override bool TryComputeLength(out long length) { length = 0; return false; }
    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => stream.WriteAsync(Encoding.UTF8.GetBytes(new string('x', 2048))).AsTask();
}
