using System.Net.Sockets;
using VpnSample.Os;
using VpnSample.Protocol;

var network = new TunnelNetwork();

if (args is ["--print-route-probes"])
{
    Console.WriteLine($"{network.Ipv4RouteProbe} {network.Ipv6RouteProbe}");
    return;
}

if (args.Length != 2)
{
    Console.WriteLine("Usage: sudo dotnet run --project Client -- <server> <port>");
    return;
}

string server = args[0];
int port = int.Parse(args[1]);

using var tcpClient = new TcpClient();
await tcpClient.ConnectAsync(server, port);
NetworkStream transport = tcpClient.GetStream();

var assignment = new byte[1];
await transport.ReadExactlyAsync(assignment);
int clientNumber = assignment[0];
TunnelAddresses addresses = network.GetAddresses(clientNumber);

await using var tun = await LinuxTunDevice.OpenAsync(new LinuxTunOptions(
    Name: network.ClientInterfaceName,
    Ipv4Address: $"{addresses.ClientIpv4}/{network.Ipv4InterfacePrefixLength}",
    Ipv4Peer: addresses.ServerIpv4,
    Ipv6Address: $"{addresses.ClientIpv6}/{network.Ipv6InterfacePrefixLength}"));

Console.WriteLine($"Connected as client {clientNumber}.");
Console.WriteLine($"IPv4: {addresses.ClientIpv4} -> {addresses.ServerIpv4}");
Console.WriteLine($"IPv6: {addresses.ClientIpv6} -> {addresses.ServerIpv6}");
var protocol = new PacketTunnelProtocol("client");
await protocol.RunAsync(tun, transport);
