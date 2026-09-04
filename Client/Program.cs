using System.Security.Cryptography.X509Certificates;
using VpnSample.Dns;
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

if (args.Length is < 2 or > 3)
{
    Console.WriteLine("Usage: sudo dotnet run --project Client -- <server> <port> [node-name]");
    return;
}

string server = args[0];
int port = int.Parse(args[1]);
string nodeName = DnsName.NormalizeNodeName(
    args.Length == 3 ? args[2] : Environment.MachineName);
string profileName = Environment.GetEnvironmentVariable("VPN_PROFILE") ??
    TunnelProfileFactory.DefaultProfileName;
if (!TunnelProfileFactory.IsSupported(profileName))
    throw new ArgumentException($"Unknown tunnel profile: '{profileName}'.", nameof(profileName));

string tlsServerName = Environment.GetEnvironmentVariable("VPN_TLS_SERVER_NAME") ??
    network.DefaultTlsServerName;
string? accessToken = Environment.GetEnvironmentVariable("VPN_COVER_TOKEN");
if (string.IsNullOrWhiteSpace(accessToken))
    throw new InvalidOperationException("VPN_COVER_TOKEN must contain the WebSocket access token.");
string? pinnedCertificatePath =
    Environment.GetEnvironmentVariable("VPN_TLS_PINNED_CERTIFICATE");
using X509Certificate2? pinnedCertificate = string.IsNullOrWhiteSpace(pinnedCertificatePath)
    ? null
    : X509CertificateLoader.LoadCertificateFromFile(pinnedCertificatePath);
await using WebSocketDuplexStream webSocket = await WebSocketTunnelTransport.ConnectAsync(
    server,
    port,
    tlsServerName,
    accessToken,
    pinnedCertificate);

await NodeRegistrationProtocol.WriteRequestAsync(webSocket, nodeName);
int clientNumber = await NodeRegistrationProtocol.ReadResponseAsync(webSocket);
TunnelAddresses addresses = network.GetAddresses(clientNumber);

await using var tun = await LinuxTunDevice.OpenAsync(new LinuxTunOptions(
    Name: network.ClientInterfaceName,
    Ipv4Address: $"{addresses.ClientIpv4}/{network.Ipv4InterfacePrefixLength}",
    Ipv6Address: $"{addresses.ClientIpv6}/{network.Ipv6InterfacePrefixLength}"));

Console.WriteLine($"Connected as client {clientNumber}.");
Console.WriteLine($"DNS name: {DnsName.GetFullName(nodeName)}");
Console.WriteLine($"IPv4: {addresses.ClientIpv4} in {network.Ipv4Network}");
Console.WriteLine($"IPv6: {addresses.ClientIpv6} in {network.Ipv6Network}");
Console.WriteLine(
    $"WebSocket transport: wss://{tlsServerName}:{port}{WebSocketTunnelTransport.Path}");
Console.WriteLine($"Tunnel profile: {profileName}");
await using var pipeline = TunnelProfileFactory.Create(profileName, "client");
await pipeline.RunAsync(tun, webSocket);
