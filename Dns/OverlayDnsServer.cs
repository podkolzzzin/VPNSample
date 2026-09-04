using System.Net;
using System.Net.Sockets;

namespace VpnSample.Dns;

public sealed class OverlayDnsServer : IAsyncDisposable
{
    readonly UdpClient udp;
    readonly OverlayDnsRegistry registry;

    public OverlayDnsServer(IPEndPoint endpoint, OverlayDnsRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(registry);
        udp = new UdpClient(endpoint);
        this.registry = registry;
    }

    public IPEndPoint LocalEndpoint => (IPEndPoint)udp.Client.LocalEndPoint!;

    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            UdpReceiveResult query = await udp.ReceiveAsync(cancellationToken);
            byte[]? response = DnsMessage.CreateResponse(query.Buffer, registry);
            if (response is not null)
                await udp.SendAsync(response, query.RemoteEndPoint, cancellationToken);
        }
    }

    public ValueTask DisposeAsync()
    {
        udp.Dispose();
        return ValueTask.CompletedTask;
    }
}
