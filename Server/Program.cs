using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using VpnSample.Os;
using VpnSample.Protocol;

var network = new TunnelNetwork();

if (args is ["--print-networks"])
{
    Console.WriteLine($"{network.Ipv4Network} {network.Ipv6Network}");
    return;
}

int port = args.Length == 0 ? network.DefaultPort : int.Parse(args[0]);
string profileName = Environment.GetEnvironmentVariable("VPN_PROFILE") ??
    TunnelProfileFactory.DefaultProfileName;
if (!TunnelProfileFactory.IsSupported(profileName))
    throw new ArgumentException($"Unknown tunnel profile: '{profileName}'.", nameof(profileName));
string tlsServerName = Environment.GetEnvironmentVariable("VPN_TLS_SERVER_NAME") ??
    network.DefaultTlsServerName;
string certificatePath = Environment.GetEnvironmentVariable("VPN_TLS_CERTIFICATE") ??
    throw new InvalidOperationException("VPN_TLS_CERTIFICATE must point to a PEM certificate.");
string privateKeyPath = Environment.GetEnvironmentVariable("VPN_TLS_PRIVATE_KEY") ??
    throw new InvalidOperationException("VPN_TLS_PRIVATE_KEY must point to its PEM private key.");
string? accessToken = Environment.GetEnvironmentVariable("VPN_COVER_TOKEN");
if (string.IsNullOrWhiteSpace(accessToken))
    throw new InvalidOperationException("VPN_COVER_TOKEN must contain the WebSocket access token.");
using X509Certificate2 certificate = X509Certificate2.CreateFromPemFile(
    certificatePath,
    privateKeyPath);

await using var exitNode = await LinuxTunDevice.OpenAsync(new LinuxTunOptions(
    Name: network.ServerInterfaceName,
    Ipv4Address: $"{network.ServerIpv4}/{network.Ipv4InterfacePrefixLength}",
    Ipv6Address: $"{network.ServerIpv6}/{network.Ipv6InterfacePrefixLength}"));
var router = new TunnelPacketRouter(exitNode, network);
Task exitNodeRouting = router.RouteExitNodePacketsAsync();
var clientSlots = new bool[network.ClientCapacity];

WebApplicationBuilder builder = WebApplication.CreateSlimBuilder();
builder.WebHost.ConfigureKestrel(options =>
{
    options.AddServerHeader = false;
    options.ListenAnyIP(port, listen =>
    {
        listen.Protocols = HttpProtocols.Http1AndHttp2;
        listen.UseHttps(certificate);
    });
});
WebApplication app = builder.Build();
app.UseWebSockets(new WebSocketOptions
{
    KeepAliveInterval = TimeSpan.FromSeconds(30)
});

string coverPage = await File.ReadAllTextAsync(
    Path.Combine(AppContext.BaseDirectory, "CoverPage.html"));
app.MapGet("/", () => Results.Content(coverPage, "text/html; charset=utf-8"));
app.MapGet(WebSocketTunnelTransport.Path, HandleClientAsync);
app.MapFallback(() => Results.NotFound());

Console.WriteLine(
    $"Serving https://{tlsServerName}:{port}/ with a protected WebSocket tunnel " +
    $"using profile '{profileName}', overlay {network.Ipv4Network} / {network.Ipv6Network}");

Task webServer = app.RunAsync();
Task completed = await Task.WhenAny(webServer, exitNodeRouting);
if (completed == exitNodeRouting)
{
    await app.StopAsync();
    await exitNodeRouting;
}
await webServer;

async Task HandleClientAsync(HttpContext context)
{
    if (!context.WebSockets.IsWebSocketRequest || !HasAccessToken(context, accessToken))
    {
        context.Response.StatusCode = StatusCodes.Status404NotFound;
        return;
    }

    int clientNumber;
    lock (clientSlots)
    {
        clientNumber = Array.FindIndex(clientSlots, isUsed => !isUsed);
        if (clientNumber >= 0)
            clientSlots[clientNumber] = true;
    }

    if (clientNumber < 0)
    {
        context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        return;
    }

    try
    {
        WebSocket socket = await context.WebSockets.AcceptWebSocketAsync();
        await using var transport = new WebSocketDuplexStream(socket);
        TunnelAddresses addresses = network.GetAddresses(clientNumber);
        await using RoutedPacketEndpoint peer = router.RegisterPeer(addresses);

        await transport.WriteAsync(new[] { checked((byte)clientNumber) });
        Console.WriteLine(
            $"Client {clientNumber} connected: {context.Connection.RemoteIpAddress}, " +
            $"{addresses.ClientIpv4}, {addresses.ClientIpv6}");
        await using var pipeline = TunnelProfileFactory.Create(
            profileName,
            $"server client={clientNumber}");
        await pipeline.RunAsync(peer, transport, context.RequestAborted);
    }
    catch (Exception error)
    {
        Console.WriteLine($"Client {clientNumber} disconnected: {error.Message}");
    }
    finally
    {
        lock (clientSlots)
            clientSlots[clientNumber] = false;
    }
}

static bool HasAccessToken(HttpContext context, string expectedToken)
{
    const string prefix = "Bearer ";
    string header = context.Request.Headers.Authorization.ToString();
    if (!header.StartsWith(prefix, StringComparison.Ordinal))
        return false;

    byte[] presented = Encoding.UTF8.GetBytes(header[prefix.Length..]);
    byte[] expected = Encoding.UTF8.GetBytes(expectedToken);
    return presented.Length == expected.Length &&
        CryptographicOperations.FixedTimeEquals(presented, expected);
}
