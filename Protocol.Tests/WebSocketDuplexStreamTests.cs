using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using VpnSample.Protocol;

namespace VpnSample.Protocol.Tests;

public sealed class WebSocketDuplexStreamTests
{
    [Fact]
    public async Task TransfersBinaryDataInBothDirectionsAsAStream()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var clientTcp = new TcpClient();
        Task<TcpClient> accept = listener.AcceptTcpClientAsync();
        await clientTcp.ConnectAsync((IPEndPoint)listener.LocalEndpoint);
        using TcpClient serverTcp = await accept;
        listener.Stop();

        using (clientTcp)
        using (WebSocket clientWebSocket = WebSocket.CreateFromStream(
            clientTcp.GetStream(), isServer: false, subProtocol: null, TimeSpan.FromSeconds(30)))
        using (WebSocket serverWebSocket = WebSocket.CreateFromStream(
            serverTcp.GetStream(), isServer: true, subProtocol: null, TimeSpan.FromSeconds(30)))
        await using (var client = new WebSocketDuplexStream(clientWebSocket))
        await using (var server = new WebSocketDuplexStream(serverWebSocket))
        using (var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
        {
            byte[] request = Enumerable.Range(0, 700).Select(value => (byte)value).ToArray();
            byte[] response = Enumerable.Repeat((byte)0xA5, 300).ToArray();

            await client.WriteAsync(request, stop.Token);
            var receivedRequest = new byte[request.Length];
            await server.ReadExactlyAsync(receivedRequest, stop.Token);
            Assert.Equal(request, receivedRequest);

            await server.WriteAsync(response, stop.Token);
            var receivedResponse = new byte[response.Length];
            await client.ReadExactlyAsync(receivedResponse, stop.Token);
            Assert.Equal(response, receivedResponse);
        }
    }
}
