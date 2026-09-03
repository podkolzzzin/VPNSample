using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using VpnSample.Protocol;

namespace VpnSample.Protocol.Tests;

public sealed class HttpsTunnelTransportTests
{
    [Fact]
    public async Task EstablishesPinnedTlsAndHttpUpgrade()
    {
        const string serverName = "vpn.twocubes.io";
        using X509Certificate2 certificate = CreateCertificate(serverName);
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var client = new TcpClient();
        Task<TcpClient> accept = listener.AcceptTcpClientAsync();
        await client.ConnectAsync((IPEndPoint)listener.LocalEndpoint);
        using TcpClient server = await accept;
        listener.Stop();
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        Task<System.Net.Security.SslStream> serverTls = HttpsTunnelTransport.AcceptAsync(
            server.GetStream(), certificate, serverName, stop.Token);
        Task<System.Net.Security.SslStream> clientTls = HttpsTunnelTransport.ConnectAsync(
            client.GetStream(), serverName, certificate, stop.Token);
        await Task.WhenAll(serverTls, clientTls);
        await using System.Net.Security.SslStream accepted = await serverTls;
        await using System.Net.Security.SslStream connected = await clientTls;

        byte[] expected = [1, 2, 3, 4];
        await connected.WriteAsync(expected, stop.Token);
        var actual = new byte[expected.Length];
        await accepted.ReadExactlyAsync(actual, stop.Token);

        Assert.True(connected.IsEncrypted);
        Assert.True(accepted.IsEncrypted);
        Assert.Equal(expected, actual);
    }

    static X509Certificate2 CreateCertificate(string serverName)
    {
        using RSA key = RSA.Create(2048);
        var request = new CertificateRequest(
            $"CN={serverName}",
            key,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        var names = new SubjectAlternativeNameBuilder();
        names.AddDnsName(serverName);
        request.CertificateExtensions.Add(names.Build());
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment,
            false));
        return request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddMinutes(-1),
            DateTimeOffset.UtcNow.AddDays(1));
    }
}
