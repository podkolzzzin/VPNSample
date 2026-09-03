using System.Net;
using System.Net.Sockets;
using VpnSample.Os;
using VpnSample.Protocol;

var network = new TunnelNetwork();

if (args is ["--print-networks"])
{
    Console.WriteLine($"{network.Ipv4Network} {network.Ipv6Network}");
    return;
}

int port = args.Length == 0 ? network.DefaultPort : int.Parse(args[0]);
var listener = new TcpListener(IPAddress.Any, port);
listener.Start();
var clientSlots = new bool[network.ClientCapacity];

Console.WriteLine($"Listening on {listener.LocalEndpoint}");

while (true)
{
    TcpClient tcpClient = await listener.AcceptTcpClientAsync();
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
            TunnelAddresses addresses = network.GetAddresses(clientNumber);

            await using var tun = await LinuxTunDevice.OpenAsync(new LinuxTunOptions(
                Name: network.GetServerInterfaceName(clientNumber),
                Ipv4Address: $"{addresses.ServerIpv4}/{network.Ipv4InterfacePrefixLength}",
                Ipv4Peer: addresses.ClientIpv4,
                Ipv6Address: $"{addresses.ServerIpv6}/{network.Ipv6InterfacePrefixLength}"));

            await transport.WriteAsync(new[] { checked((byte)clientNumber) });
            Console.WriteLine($"Client {clientNumber} connected: {tcpClient.Client.RemoteEndPoint}");
            var protocol = new PacketTunnelProtocol($"server client={clientNumber}");
            await protocol.RunAsync(tun, transport);
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
