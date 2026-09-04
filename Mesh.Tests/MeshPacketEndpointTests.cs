using System.Collections.Concurrent;
using System.Net;
using System.Threading.Channels;
using VpnSample.Mesh;
using VpnSample.Protocol;

namespace VpnSample.Mesh.Tests;

public sealed class MeshPacketEndpointTests
{
    [Theory]
    [InlineData("10.8.0.2", false)]
    [InlineData("127.0.0.1", false)]
    [InlineData("192.0.2.10", true)]
    public void ExcludesOverlayAndLoopbackAddressesFromUnderlayCandidates(
        string address,
        bool expected)
    {
        Assert.Equal(expected, MeshPacketEndpoint.IsUnderlayCandidate(
            IPAddress.Parse(address),
            IPAddress.Parse("10.8.0.2")));
    }

    [Fact]
    public async Task SendsPacketsWithoutDirectPathToRelayFallback()
    {
        using var tun = new TestPacketEndpoint();
        await using var mesh = new MeshPacketEndpoint(
            tun, "alice", "10.8.0.2", "fd42:8::2");
        var coordinator = new MeshCoordinator();
        string token = coordinator.RegisterDataSession(
            "alice",
            new TunnelNetwork().GetAddresses(0));
        await using var rendezvous = new UdpRendezvousServer(
            new IPEndPoint(IPAddress.Loopback, 0),
            coordinator);
        using var rendezvousStop = new CancellationTokenSource();
        Task rendezvousTask = rendezvous.RunAsync(rendezvousStop.Token);
        await mesh.StartAsync(rendezvous.LocalEndpoint, token);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        byte[] packet = Ipv4Packet("10.8.0.2", "1.1.1.1");

        await tun.EnqueueOutboundAsync(packet, timeout.Token);
        var relayed = new byte[packet.Length];
        await mesh.PacketReader.ReadExactlyAsync(relayed, timeout.Token);

        Assert.Equal(packet, relayed);
        rendezvousStop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => rendezvousTask);
    }

    [Fact]
    public async Task RoutesPeerPacketsOverEncryptedUdpWithoutRelay()
    {
        using var aliceTun = new TestPacketEndpoint();
        using var bobTun = new TestPacketEndpoint();
        var aliceLog = new ConcurrentQueue<string>();
        var bobLog = new ConcurrentQueue<string>();
        await using var alice = new MeshPacketEndpoint(
            aliceTun, "alice", "10.8.0.2", "fd42:8::2", aliceLog.Enqueue);
        await using var bob = new MeshPacketEndpoint(
            bobTun, "bob", "10.8.0.3", "fd42:8::3", bobLog.Enqueue);

        alice.UpdatePeers(new MeshSnapshot([
            Descriptor("alice", "10.8.0.2", "fd42:8::2", alice),
            Descriptor("bob", "10.8.0.3", "fd42:8::3", bob)
        ]));
        bob.UpdatePeers(new MeshSnapshot([
            Descriptor("alice", "10.8.0.2", "fd42:8::2", alice),
            Descriptor("bob", "10.8.0.3", "fd42:8::3", bob)
        ]));

        var coordinator = new MeshCoordinator();
        var network = new TunnelNetwork();
        string aliceToken = coordinator.RegisterDataSession("alice", network.GetAddresses(0));
        string bobToken = coordinator.RegisterDataSession("bob", network.GetAddresses(1));
        await using var rendezvous = new UdpRendezvousServer(
            new IPEndPoint(IPAddress.Loopback, 0),
            coordinator);
        using var rendezvousStop = new CancellationTokenSource();
        Task rendezvousTask = rendezvous.RunAsync(rendezvousStop.Token);

        await alice.StartAsync(rendezvous.LocalEndpoint, aliceToken);
        await bob.StartAsync(rendezvous.LocalEndpoint, bobToken);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await WaitUntilAsync(
            () => aliceLog.Any(line => line.StartsWith("Direct mesh path: bob.vpn", StringComparison.Ordinal)) &&
                bobLog.Any(line => line.StartsWith("Direct mesh path: alice.vpn", StringComparison.Ordinal)),
            timeout.Token);

        byte[] packet = Ipv4Packet("10.8.0.2", "10.8.0.3");
        await aliceTun.EnqueueOutboundAsync(packet, timeout.Token);

        Assert.Equal(packet, await bobTun.ReadInboundAsync(timeout.Token));
        using var noRelay = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        var relayBuffer = new byte[1];
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await alice.PacketReader.ReadExactlyAsync(relayBuffer, noRelay.Token));

        rendezvousStop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => rendezvousTask);
    }

    static MeshPeerDescriptor Descriptor(
        string name,
        string ipv4,
        string ipv6,
        MeshPacketEndpoint endpoint) =>
        new(name, ipv4, ipv6, endpoint.PublicKey, null,
            [$"127.0.0.1:{endpoint.UdpPort}"]);

    static async Task WaitUntilAsync(Func<bool> condition, CancellationToken cancellationToken)
    {
        while (!condition())
            await Task.Delay(20, cancellationToken);
    }

    static byte[] Ipv4Packet(string source, string destination)
    {
        var packet = new byte[20];
        packet[0] = 0x45;
        IPAddress.Parse(source).GetAddressBytes().CopyTo(packet, 12);
        IPAddress.Parse(destination).GetAddressBytes().CopyTo(packet, 16);
        return packet;
    }

    sealed class TestPacketEndpoint : IPacketEndpoint, IDisposable
    {
        readonly PacketReadStream outgoing = new();
        readonly PacketWriteStream incoming = new();

        public Stream PacketReader => outgoing;
        public Stream PacketWriter => incoming;
        public ValueTask EnqueueOutboundAsync(byte[] packet, CancellationToken cancellationToken) =>
            outgoing.EnqueueAsync(packet, cancellationToken);
        public ValueTask<byte[]> ReadInboundAsync(CancellationToken cancellationToken) =>
            incoming.ReadAsync(cancellationToken);
        public ValueTask InterruptReadAsync()
        {
            outgoing.Complete();
            return ValueTask.CompletedTask;
        }
        public void Dispose()
        {
            outgoing.Dispose();
            incoming.Dispose();
        }
    }

    sealed class PacketReadStream : Stream
    {
        readonly Channel<byte[]> packets = Channel.CreateUnbounded<byte[]>();
        public ValueTask EnqueueAsync(byte[] packet, CancellationToken cancellationToken) =>
            packets.Writer.WriteAsync(packet, cancellationToken);
        public void Complete() => packets.Writer.TryComplete();
        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            try
            {
                byte[] packet = await packets.Reader.ReadAsync(cancellationToken);
                packet.CopyTo(buffer);
                return packet.Length;
            }
            catch (ChannelClosedException)
            {
                return 0;
            }
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing)
                Complete();
            base.Dispose(disposing);
        }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }
        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    sealed class PacketWriteStream : Stream
    {
        readonly Channel<byte[]> packets = Channel.CreateUnbounded<byte[]>();
        public ValueTask<byte[]> ReadAsync(CancellationToken cancellationToken) =>
            packets.Reader.ReadAsync(cancellationToken);
        public override ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default) =>
            packets.Writer.WriteAsync(buffer.ToArray(), cancellationToken);
        protected override void Dispose(bool disposing)
        {
            if (disposing)
                packets.Writer.TryComplete();
            base.Dispose(disposing);
        }
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }
        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
