using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using VpnSample.Os;
using VpnSample.Protocol;

var network = new TunnelNetwork();

if (args is ["--print-route-probes"])
{
    Console.WriteLine($"{network.Ipv4RouteProbe} {network.Ipv6RouteProbe}");
    return;
}

if (args is ["--print-server-addresses"])
{
    Console.WriteLine($"{network.ServerIpv4} {network.ServerIpv6}");
    return;
}

if (args.Length != 2)
{
    Console.WriteLine("Usage: sudo dotnet run --project Client -- <server> <port>");
    return;
}

string server = args[0];
int port = int.Parse(args[1]);
string profileName = Environment.GetEnvironmentVariable("VPN_PROFILE") ??
    TunnelProfileFactory.BaselineProfileName;
if (!TunnelProfileFactory.IsSupported(profileName))
    throw new ArgumentException($"Unknown tunnel profile: '{profileName}'.", nameof(profileName));

using var tcpClient = new TcpClient();
await tcpClient.ConnectAsync(server, port);
NetworkStream transport = tcpClient.GetStream();
string tlsServerName = Environment.GetEnvironmentVariable("VPN_TLS_SERVER_NAME") ??
    network.DefaultTlsServerName;
string? pinnedCertificatePath =
    Environment.GetEnvironmentVariable("VPN_TLS_PINNED_CERTIFICATE");
using X509Certificate2? pinnedCertificate = string.IsNullOrWhiteSpace(pinnedCertificatePath)
    ? null
    : X509CertificateLoader.LoadCertificateFromFile(pinnedCertificatePath);
await using var https = await HttpsTunnelTransport.ConnectAsync(
    transport,
    tlsServerName,
    pinnedCertificate);

var assignment = new byte[1];
await https.ReadExactlyAsync(assignment);
int clientNumber = assignment[0];
TunnelAddresses addresses = network.GetAddresses(clientNumber);

await using var tun = await LinuxTunDevice.OpenAsync(new LinuxTunOptions(
    Name: network.ClientInterfaceName,
    Ipv4Address: $"{addresses.ClientIpv4}/{network.Ipv4InterfacePrefixLength}",
    Ipv6Address: $"{addresses.ClientIpv6}/{network.Ipv6InterfacePrefixLength}"));

Console.WriteLine($"Connected as client {clientNumber}.");
Console.WriteLine($"IPv4: {addresses.ClientIpv4} in {network.Ipv4Network}");
Console.WriteLine($"IPv6: {addresses.ClientIpv6} in {network.Ipv6Network}");
Console.WriteLine($"HTTPS transport: https://{tlsServerName}:{port}/vpn");
Console.WriteLine($"Tunnel profile: {profileName}");
await using var pipeline = TunnelProfileFactory.Create(profileName, "client");
await pipeline.RunAsync(tun, https);
