using System.Net;
using System.Threading.Channels;
using VpnSample.Protocol;

namespace VpnSample.Protocol.Tests;

public sealed class TunnelPacketRouterTests
{
    [Fact]
    public async Task RoutesOverlayPacketsDirectlyBetweenPeers()
    {
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        using var exitNode = new TestPacketEndpoint();
        var network = new TunnelNetwork();
        var router = new TunnelPacketRouter(exitNode, network);
        await using RoutedPacketEndpoint peerA = router.RegisterPeer(network.GetAddresses(0));
        await using RoutedPacketEndpoint peerB = router.RegisterPeer(network.GetAddresses(1));
        byte[] packet = Ipv4Packet("10.8.0.2", "10.8.0.3");

        await peerA.PacketWriter.WriteAsync(packet, stop.Token);

        Assert.Equal(packet, await ReadPacketAsync(peerB.PacketReader, stop.Token));
        Assert.False(exitNode.HasWrittenPacket);
    }

    [Fact]
    public async Task SendsInternetPacketsToTheExitNode()
    {
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        using var exitNode = new TestPacketEndpoint();
        var network = new TunnelNetwork();
        var router = new TunnelPacketRouter(exitNode, network);
        await using RoutedPacketEndpoint peer = router.RegisterPeer(network.GetAddresses(0));
        byte[] packet = Ipv4Packet("10.8.0.2", "1.1.1.1");

        await peer.PacketWriter.WriteAsync(packet, stop.Token);

        Assert.Equal(packet, await exitNode.ReadWrittenPacketAsync(stop.Token));
    }

    [Fact]
    public async Task RoutesExitNodeResponsesBackToTheDestinationPeer()
    {
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        using var exitNode = new TestPacketEndpoint();
        var network = new TunnelNetwork();
        var router = new TunnelPacketRouter(exitNode, network);
        await using RoutedPacketEndpoint peer = router.RegisterPeer(network.GetAddresses(0));
        byte[] packet = Ipv6Packet("2606:4700:4700::1111", "fd42:8::2");
        Task route = router.RouteExitNodePacketsAsync(stop.Token);

        await exitNode.EnqueueAsync(packet, stop.Token);

        Assert.Equal(packet, await ReadPacketAsync(peer.PacketReader, stop.Token));
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => route);
    }

    [Fact]
    public async Task RejectsSpoofedPeerSources()
    {
        using var exitNode = new TestPacketEndpoint();
        var network = new TunnelNetwork();
        var router = new TunnelPacketRouter(exitNode, network);
        await using RoutedPacketEndpoint peer = router.RegisterPeer(network.GetAddresses(0));
        byte[] spoofed = Ipv4Packet("10.8.0.99", "1.1.1.1");

        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await peer.PacketWriter.WriteAsync(spoofed));
    }

    [Fact]
    public async Task IgnoresAutomaticLinkLocalControlPackets()
    {
        using var exitNode = new TestPacketEndpoint();
        var network = new TunnelNetwork();
        var router = new TunnelPacketRouter(exitNode, network);
        await using RoutedPacketEndpoint peer = router.RegisterPeer(network.GetAddresses(0));

        await peer.PacketWriter.WriteAsync(Ipv6Packet("fe80::1", "ff02::1"));

        Assert.False(exitNode.HasWrittenPacket);
    }

    [Fact]
    public async Task DropsPacketsForUnassignedOverlayAddresses()
    {
        using var exitNode = new TestPacketEndpoint();
        var network = new TunnelNetwork();
        var router = new TunnelPacketRouter(exitNode, network);
        await using RoutedPacketEndpoint peer = router.RegisterPeer(network.GetAddresses(0));

        await peer.PacketWriter.WriteAsync(Ipv4Packet("10.8.0.2", "10.8.0.99"));

        Assert.False(exitNode.HasWrittenPacket);
    }

    static async Task<byte[]> ReadPacketAsync(Stream stream, CancellationToken cancellationToken)
    {
        var buffer = new byte[ushort.MaxValue];
        int length = await stream.ReadAsync(buffer, cancellationToken);
        return buffer.AsSpan(0, length).ToArray();
    }

    static byte[] Ipv4Packet(string source, string destination)
    {
        var packet = new byte[20];
        packet[0] = 0x45;
        IPAddress.Parse(source).GetAddressBytes().CopyTo(packet, 12);
        IPAddress.Parse(destination).GetAddressBytes().CopyTo(packet, 16);
        return packet;
    }

    static byte[] Ipv6Packet(string source, string destination)
    {
        var packet = new byte[40];
        packet[0] = 0x60;
        IPAddress.Parse(source).GetAddressBytes().CopyTo(packet, 8);
        IPAddress.Parse(destination).GetAddressBytes().CopyTo(packet, 24);
        return packet;
    }

    sealed class TestPacketEndpoint : IPacketEndpoint, IDisposable
    {
        readonly TestReadStream reader = new();
        readonly TestWriteStream writer = new();

        public Stream PacketReader => reader;
        public Stream PacketWriter => writer;
        public bool HasWrittenPacket => writer.HasPacket;

        public ValueTask EnqueueAsync(byte[] packet, CancellationToken cancellationToken) =>
            reader.EnqueueAsync(packet, cancellationToken);

        public ValueTask<byte[]> ReadWrittenPacketAsync(CancellationToken cancellationToken) =>
            writer.ReadPacketAsync(cancellationToken);

        public ValueTask InterruptReadAsync()
        {
            reader.Complete();
            return ValueTask.CompletedTask;
        }

        public void Dispose()
        {
            reader.Dispose();
            writer.Dispose();
        }
    }

    sealed class TestReadStream : Stream
    {
        readonly Channel<byte[]> packets = Channel.CreateUnbounded<byte[]>();

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public ValueTask EnqueueAsync(byte[] packet, CancellationToken cancellationToken) =>
            packets.Writer.WriteAsync(packet, cancellationToken);
        public void Complete() => packets.Writer.TryComplete();

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            byte[] packet = await packets.Reader.ReadAsync(cancellationToken);
            packet.CopyTo(buffer);
            return packet.Length;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                Complete();
            base.Dispose(disposing);
        }

        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    sealed class TestWriteStream : Stream
    {
        readonly Channel<byte[]> packets = Channel.CreateUnbounded<byte[]>();

        public bool HasPacket => packets.Reader.TryPeek(out _);
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public ValueTask<byte[]> ReadPacketAsync(CancellationToken cancellationToken) =>
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

        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
