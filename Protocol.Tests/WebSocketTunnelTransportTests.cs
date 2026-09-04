using System.Net;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using VpnSample.Protocol;

namespace VpnSample.Protocol.Tests;

public sealed class WebSocketTunnelTransportTests
{
    [Fact]
    public async Task ConnectsToPinnedWebSocketAtAnotherNetworkAddress()
    {
        const string serverName = "vpn.twocubes.io";
        const string accessToken = "0123456789abcdef0123456789abcdef";
        using X509Certificate2 certificate = CreateCertificate(serverName);
        WebApplicationBuilder builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options =>
            options.Listen(IPAddress.Loopback, 0, listen => listen.UseHttps(certificate)));
        WebApplication app = builder.Build();
        app.UseWebSockets();
        app.MapGet(WebSocketTunnelTransport.Path, async context =>
        {
            Assert.Equal($"Bearer {accessToken}", context.Request.Headers.Authorization);
            using WebSocket socket = await context.WebSockets.AcceptWebSocketAsync();
            await using var transport = new WebSocketDuplexStream(socket);
            var request = new byte[4];
            await transport.ReadExactlyAsync(request, context.RequestAborted);
            await transport.WriteAsync(request.Reverse().ToArray(), context.RequestAborted);
        });

        await app.StartAsync();
        try
        {
            IServer server = app.Services.GetRequiredService<IServer>();
            string address = Assert.Single(
                server.Features.Get<IServerAddressesFeature>()!.Addresses);
            int port = new Uri(address).Port;
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await using WebSocketDuplexStream client =
                await WebSocketTunnelTransport.ConnectAsync(
                    IPAddress.Loopback.ToString(),
                    port,
                    serverName,
                    accessToken,
                    certificate,
                    stop.Token);

            await client.WriteAsync(new byte[] { 1, 2, 3, 4 }, stop.Token);
            var response = new byte[4];
            await client.ReadExactlyAsync(response, stop.Token);

            Assert.Equal(new byte[] { 4, 3, 2, 1 }, response);
        }
        finally
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
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
