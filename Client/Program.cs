using System.Security.Cryptography.X509Certificates;
using System.Net;
using System.Net.Sockets;
using VpnSample.Dns;
using VpnSample.Mesh;
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
string meshIdentityPath = Environment.GetEnvironmentVariable("VPN_MESH_KEY_FILE") ??
    Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".vpnsample",
        "mesh-identity.key");
int? meshSocketMark = ReadOptionalPositiveInteger("VPN_MESH_SOCKET_MARK");
Action<Socket>? configureMeshSocket = meshSocketMark is int mark
    ? socket => LinuxSocketOptions.SetMark(socket, mark)
    : null;
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
string meshSessionToken = await MeshSessionProtocol.ReadAsync(webSocket);
TunnelAddresses addresses = network.GetAddresses(clientNumber);

await using var tun = await LinuxTunDevice.OpenAsync(new LinuxTunOptions(
    Name: network.ClientInterfaceName,
    Ipv4Address: $"{addresses.ClientIpv4}/{network.Ipv4InterfacePrefixLength}",
    Ipv6Address: $"{addresses.ClientIpv6}/{network.Ipv6InterfacePrefixLength}",
    Mtu: network.OverlayMtu));
await using var mesh = new MeshPacketEndpoint(
    tun,
    nodeName,
    addresses.ClientIpv4,
    addresses.ClientIpv6,
    identityPath: meshIdentityPath,
    configureUdpSocket: configureMeshSocket);
var controlHeaders = new Dictionary<string, string>
{
    [MeshControlProtocol.SessionHeader] = meshSessionToken
};
await using WebSocketDuplexStream meshControl =
    await WebSocketTunnelTransport.ConnectToPathAsync(
        server,
        port,
        tlsServerName,
        accessToken,
        MeshControlProtocol.Path,
        pinnedCertificate,
        controlHeaders);
await MeshControlProtocol.WriteRegistrationAsync(
    meshControl,
    new MeshRegistrationRequest(nodeName, mesh.PublicKey, mesh.GetLocalCandidates()));
IPAddress rendezvousAddress = await ResolveIpv4Async(server);
await mesh.StartAsync(new IPEndPoint(rendezvousAddress, port), meshSessionToken);
using var controlStop = new CancellationTokenSource();
Task watchPeers = WatchPeersAsync(meshControl, mesh, controlStop.Token);

Console.WriteLine($"Connected as client {clientNumber}.");
Console.WriteLine($"DNS name: {DnsName.GetFullName(nodeName)}");
Console.WriteLine($"Mesh UDP: 0.0.0.0:{mesh.UdpPort}, rendezvous {rendezvousAddress}:{port}");
Console.WriteLine($"IPv4: {addresses.ClientIpv4} in {network.Ipv4Network}");
Console.WriteLine($"IPv6: {addresses.ClientIpv6} in {network.Ipv6Network}");
Console.WriteLine(
    $"WebSocket transport: wss://{tlsServerName}:{port}{WebSocketTunnelTransport.Path}");
Console.WriteLine($"Tunnel profile: {profileName}");
await using var pipeline = TunnelProfileFactory.Create(profileName, "client");
try
{
    await pipeline.RunAsync(mesh, webSocket);
}
finally
{
    controlStop.Cancel();
    try
    {
        await watchPeers;
    }
    catch (OperationCanceledException)
    {
    }
}

static async Task<IPAddress> ResolveIpv4Async(string host)
{
    if (IPAddress.TryParse(host, out IPAddress? address))
    {
        if (address.AddressFamily != AddressFamily.InterNetwork)
            throw new InvalidOperationException("The mesh rendezvous currently requires an IPv4 server address.");
        return address;
    }

    IPAddress? resolved = (await System.Net.Dns.GetHostAddressesAsync(host))
        .FirstOrDefault(candidate => candidate.AddressFamily == AddressFamily.InterNetwork);
    return resolved ?? throw new InvalidOperationException(
        $"Could not resolve an IPv4 rendezvous address for {host}.");
}

static int? ReadOptionalPositiveInteger(string name)
{
    string? value = Environment.GetEnvironmentVariable(name);
    if (string.IsNullOrWhiteSpace(value))
        return null;
    if (!int.TryParse(value, out int parsed) || parsed <= 0)
        throw new InvalidOperationException($"{name} must be a positive integer.");
    return parsed;
}

static async Task WatchPeersAsync(
    Stream control,
    MeshPacketEndpoint mesh,
    CancellationToken cancellationToken)
{
    while (!cancellationToken.IsCancellationRequested)
        mesh.UpdatePeers(await MeshControlProtocol.ReadSnapshotAsync(control, cancellationToken));
}
