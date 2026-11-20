using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using PocketAgent;

internal static class Program
{
    public static async Task<int> Main()
    {
        var enrollment = RelayEnrollment.Create(new Uri("wss://relay.example.test/relay"));
        var decodedEnrollment = RelayEnrollment.Parse(enrollment.ToPairingString());
        if (decodedEnrollment != enrollment) throw new InvalidOperationException("Relay enrollment pairing did not round-trip.");
        var relayPort = FreePort();
        var remotePort = FreePort();
        var hostId = Guid.NewGuid();
        var deviceId = Guid.NewGuid();
        var hostToken = "relay-check-host";
        var deviceToken = "relay-check-device";
        var key = RelaySessionKey.Create();
        WebApplication? relay = null;
        WebApplication? remote = null;
        CancellationTokenSource? hostStop = null;
        try
        {
            remote = BuildRemoteServer(remotePort);
            await remote.StartAsync();

            var node = new RelayNode();
            var auth = new RelayAuthRegistry();
            auth.RegisterHost(hostId, hostToken);
            auth.RegisterClient(deviceId, deviceToken);
            relay = BuildRelayServer(relayPort, node, auth);
            await relay.StartAsync();

            hostStop = new CancellationTokenSource();
            var host = await RelayApplicationHost.ConnectAsync(
                new Uri($"ws://127.0.0.1:{relayPort}/relay"),
                hostId,
                hostToken,
                key,
                async request =>
                {
                    using var client = new HttpClient();
                    using var message = new HttpRequestMessage(new HttpMethod(request.Method), $"http://127.0.0.1:{remotePort}{request.Path}")
                    {
                        Content = new ByteArrayContent(request.Body)
                    };
                    foreach (var header in request.Headers) message.Headers.TryAddWithoutValidation(header.Key, header.Value);
                    using var response = await client.SendAsync(message);
                    return RelayApplicationResponse.Create((int)response.StatusCode, response.Content.Headers.ContentType?.ToString() ?? "application/octet-stream", await response.Content.ReadAsByteArrayAsync());
                });
            var hostTask = host.ServeAsync(hostStop.Token);

            await using var client = await RelayApplicationClient.ConnectAsync(
                new Uri($"ws://127.0.0.1:{relayPort}/relay"), hostId, deviceId, deviceToken, key);
            var first = await client.RequestAsync(RelayApplicationRequest.Create("POST", "/echo", Encoding.UTF8.GetBytes("first")));
            var second = await client.RequestAsync(RelayApplicationRequest.Create("POST", "/echo", Encoding.UTF8.GetBytes("second")));
            if (first.Status != 200 || Encoding.UTF8.GetString(first.Body) != "first" || second.Status != 200 || Encoding.UTF8.GetString(second.Body) != "second")
                throw new InvalidOperationException("The two-server relay round trip returned an unexpected response.");

            hostStop.Cancel();
            try { await hostTask; } catch (OperationCanceledException) { }
            Console.WriteLine($"PASS two-server relay round trip through ws://127.0.0.1:{relayPort}/relay");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            return 1;
        }
        finally
        {
            hostStop?.Cancel();
            if (relay is not null) await relay.StopAsync();
            if (remote is not null) await remote.StopAsync();
        }
    }

    private static WebApplication BuildRelayServer(int port, RelayNode node, RelayAuthRegistry auth)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls($"http://127.0.0.1:{port}");
        builder.Services.AddSingleton(node);
        builder.Services.AddSingleton(auth);
        builder.Services.AddSingleton<RelayGateway>();
        var app = builder.Build();
        app.UseWebSockets();
        app.Map("/relay", async context => await context.RequestServices.GetRequiredService<RelayGateway>().HandleAsync(context));
        return app;
    }

    private static WebApplication BuildRemoteServer(int port)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls($"http://127.0.0.1:{port}");
        var app = builder.Build();
        app.MapPost("/echo", async context =>
        {
            using var reader = new StreamReader(context.Request.Body, Encoding.UTF8);
            var body = await reader.ReadToEndAsync();
            context.Response.ContentType = "text/plain";
            await context.Response.WriteAsync(body);
        });
        return app;
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}
