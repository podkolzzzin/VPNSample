using System.Net.Security;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace VpnSample.Protocol;

public static class WebSocketTunnelTransport
{
    public const string Path = "/api/v1/events";

    public static async Task<WebSocketDuplexStream> ConnectAsync(
        string connectHost,
        int port,
        string tlsServerName,
        string accessToken,
        X509Certificate2? pinnedCertificate = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectHost);
        ArgumentException.ThrowIfNullOrWhiteSpace(tlsServerName);
        ArgumentException.ThrowIfNullOrWhiteSpace(accessToken);

        var handler = new SocketsHttpHandler
        {
            ConnectCallback = async (_, token) =>
            {
                var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
                try
                {
                    await socket.ConnectAsync(connectHost, port, token);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            }
        };
        handler.SslOptions.EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13;
        handler.SslOptions.CertificateRevocationCheckMode = X509RevocationMode.NoCheck;
        if (pinnedCertificate is not null)
        {
            handler.SslOptions.RemoteCertificateValidationCallback =
                (_, certificate, _, _) => MatchesPin(certificate, pinnedCertificate);
        }

        var http = new HttpMessageInvoker(handler, disposeHandler: true);
        var webSocket = new ClientWebSocket();
        webSocket.Options.SetRequestHeader("Authorization", $"Bearer {accessToken}");
        webSocket.Options.KeepAliveInterval = TimeSpan.FromSeconds(30);
        var uri = new UriBuilder("wss", tlsServerName, port, Path).Uri;

        try
        {
            await webSocket.ConnectAsync(uri, http, cancellationToken);
            return new WebSocketDuplexStream(webSocket, http);
        }
        catch
        {
            webSocket.Dispose();
            http.Dispose();
            throw;
        }
    }

    static bool MatchesPin(X509Certificate? certificate, X509Certificate2 pinnedCertificate)
    {
        if (certificate is null)
            return false;
        using var presented = new X509Certificate2(certificate);
        return CryptographicOperations.FixedTimeEquals(
            presented.GetCertHash(HashAlgorithmName.SHA256),
            pinnedCertificate.GetCertHash(HashAlgorithmName.SHA256));
    }
}
