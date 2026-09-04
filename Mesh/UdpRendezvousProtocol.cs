using System.Net;
using System.Net.Sockets;
using System.Text;

namespace VpnSample.Mesh;

public static class UdpRendezvousProtocol
{
    static ReadOnlySpan<byte> RegistrationMagic => "SVRB"u8;
    static ReadOnlySpan<byte> AcknowledgementMagic => "SVRA"u8;

    public static byte[] CreateRegistration(string sessionToken)
    {
        MeshSessionProtocol.Validate(sessionToken);
        var datagram = new byte[RegistrationMagic.Length + MeshSessionProtocol.TokenLength];
        RegistrationMagic.CopyTo(datagram);
        Encoding.ASCII.GetBytes(sessionToken).CopyTo(datagram, RegistrationMagic.Length);
        return datagram;
    }

    public static bool TryReadRegistration(ReadOnlySpan<byte> datagram, out string sessionToken)
    {
        if (datagram.Length != RegistrationMagic.Length + MeshSessionProtocol.TokenLength ||
            !datagram[..RegistrationMagic.Length].SequenceEqual(RegistrationMagic))
        {
            sessionToken = string.Empty;
            return false;
        }

        sessionToken = Encoding.ASCII.GetString(datagram[RegistrationMagic.Length..]);
        try
        {
            MeshSessionProtocol.Validate(sessionToken);
            return true;
        }
        catch (ArgumentException)
        {
            sessionToken = string.Empty;
            return false;
        }
    }

    public static byte[] CreateAcknowledgement() => AcknowledgementMagic.ToArray();

    public static bool IsAcknowledgement(ReadOnlySpan<byte> datagram) =>
        datagram.SequenceEqual(AcknowledgementMagic);
}

public sealed class UdpRendezvousServer : IAsyncDisposable
{
    readonly UdpClient udp;
    readonly MeshCoordinator coordinator;

    public UdpRendezvousServer(IPEndPoint endpoint, MeshCoordinator coordinator)
    {
        udp = new UdpClient(endpoint);
        this.coordinator = coordinator;
    }

    public IPEndPoint LocalEndpoint => (IPEndPoint)udp.Client.LocalEndPoint!;

    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            UdpReceiveResult datagram = await udp.ReceiveAsync(cancellationToken);
            if (!UdpRendezvousProtocol.TryReadRegistration(
                    datagram.Buffer,
                    out string sessionToken) ||
                !coordinator.TryUpdateReflexiveEndpoint(sessionToken, datagram.RemoteEndPoint))
            {
                continue;
            }

            await udp.SendAsync(
                UdpRendezvousProtocol.CreateAcknowledgement(),
                datagram.RemoteEndPoint,
                cancellationToken);
        }
    }

    public ValueTask DisposeAsync()
    {
        udp.Dispose();
        return ValueTask.CompletedTask;
    }
}
