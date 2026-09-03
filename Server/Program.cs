using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
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
    TunnelProfileFactory.BaselineProfileName;
if (!TunnelProfileFactory.IsSupported(profileName))
    throw new ArgumentException($"Unknown tunnel profile: '{profileName}'.", nameof(profileName));
string tlsServerName = Environment.GetEnvironmentVariable("VPN_TLS_SERVER_NAME") ??
    network.DefaultTlsServerName;
string certificatePath = Environment.GetEnvironmentVariable("VPN_TLS_CERTIFICATE") ??
    throw new InvalidOperationException("VPN_TLS_CERTIFICATE must point to a PEM certificate.");
string privateKeyPath = Environment.GetEnvironmentVariable("VPN_TLS_PRIVATE_KEY") ??
    throw new InvalidOperationException("VPN_TLS_PRIVATE_KEY must point to its PEM private key.");
using X509Certificate2 certificate = X509Certificate2.CreateFromPemFile(
    certificatePath,
    privateKeyPath);
var listener = new TcpListener(IPAddress.Any, port);
listener.Start();
var clientSlots = new bool[network.ClientCapacity];

await using var exitNode = await LinuxTunDevice.OpenAsync(new LinuxTunOptions(
    Name: network.ServerInterfaceName,
    Ipv4Address: $"{network.ServerIpv4}/{network.Ipv4InterfacePrefixLength}",
    Ipv6Address: $"{network.ServerIpv6}/{network.Ipv6InterfacePrefixLength}"));
var router = new TunnelPacketRouter(exitNode, network);
Task exitNodeRouting = router.RouteExitNodePacketsAsync();

Console.WriteLine(
    $"Listening on {listener.LocalEndpoint} with tunnel profile '{profileName}', " +
    $"HTTPS name '{tlsServerName}', overlay {network.Ipv4Network} / {network.Ipv6Network}");

while (true)
{
    Task<TcpClient> accept = listener.AcceptTcpClientAsync();
    Task completed = await Task.WhenAny(accept, exitNodeRouting);
    if (completed == exitNodeRouting)
        await exitNodeRouting;

    TcpClient tcpClient = await accept;
    int clientNumber;

    lock (clientSlots)
    {
        clientNumber = Array.FindIndex(clientSlots, isUsed => !isUsed);
        if (clientNumber >= 0)
            clientSlots[clientNumber] = true;
    }

    if (clientNumber < 0)
    {
        Console.WriteLine($"Rejected {tcpClient.Client.RemoteEndPoint}: no client slots are available.");
        tcpClient.Dispose();
        continue;
    }

    _ = HandleClientAsync(tcpClient, clientNumber);
}

async Task HandleClientAsync(TcpClient tcpClient, int clientNumber)
{
    using (tcpClient)
    {
        try
        {
            NetworkStream transport = tcpClient.GetStream();
            await using SslStream https = await HttpsTunnelTransport.AcceptAsync(
                transport,
                certificate,
                tlsServerName);
            TunnelAddresses addresses = network.GetAddresses(clientNumber);
            await using RoutedPacketEndpoint peer = router.RegisterPeer(addresses);

            await https.WriteAsync(new[] { checked((byte)clientNumber) });
            Console.WriteLine(
                $"Client {clientNumber} connected: {tcpClient.Client.RemoteEndPoint}, " +
                $"{addresses.ClientIpv4}, {addresses.ClientIpv6}");
            await using var pipeline = TunnelProfileFactory.Create(
                profileName,
                $"server client={clientNumber}");
            await pipeline.RunAsync(peer, https);
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
}
