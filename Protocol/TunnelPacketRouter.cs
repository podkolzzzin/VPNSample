using System.Buffers;
using System.Net;
using System.Threading.Channels;

namespace VpnSample.Protocol;

public sealed class TunnelPacketRouter(IPacketEndpoint exitNode, TunnelNetwork network)
{
    readonly object routesLock = new();
    readonly Dictionary<IPAddress, RoutedPacketEndpoint> routes = [];
    readonly SemaphoreSlim exitWriterLock = new(1, 1);
    readonly IPAddress serverIpv4 = IPAddress.Parse(network.ServerIpv4);
    readonly IPAddress serverIpv6 = IPAddress.Parse(network.ServerIpv6);

    public RoutedPacketEndpoint RegisterPeer(TunnelAddresses addresses)
    {
        ArgumentNullException.ThrowIfNull(addresses);
        var ipv4 = IPAddress.Parse(addresses.ClientIpv4);
        var ipv6 = IPAddress.Parse(addresses.ClientIpv6);
        var peer = new RoutedPacketEndpoint(this, ipv4, ipv6);

        lock (routesLock)
        {
            if (routes.ContainsKey(ipv4) || routes.ContainsKey(ipv6))
                throw new InvalidOperationException("A peer with one of these overlay addresses is already registered.");
            routes.Add(ipv4, peer);
            routes.Add(ipv6, peer);
        }

        return peer;
    }

    public async Task RouteExitNodePacketsAsync(CancellationToken cancellationToken = default)
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(TunnelFrame.MaximumPayloadLength);
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int length = await exitNode.PacketReader.ReadAsync(
                    buffer.AsMemory(0, TunnelFrame.MaximumPayloadLength),
                    cancellationToken);
                if (length == 0)
                    throw new EndOfStreamException("The exit-node packet endpoint closed.");

                PacketAddresses packet = PacketAddresses.Parse(buffer.AsSpan(0, length));
                RoutedPacketEndpoint? destination = FindRoute(packet.Destination);
                if (destination is not null)
                    await destination.EnqueueAsync(buffer.AsMemory(0, length), cancellationToken);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    internal async ValueTask RoutePeerPacketAsync(
        RoutedPacketEndpoint source,
        ReadOnlyMemory<byte> packetBytes,
        CancellationToken cancellationToken)
    {
        PacketAddresses packet = PacketAddresses.Parse(packetBytes.Span);
        if (!source.Owns(packet.Source))
        {
            if (packet.Source.Equals(IPAddress.Any) ||
                packet.Source.Equals(IPAddress.IPv6Any) ||
                packet.Source.IsIPv6LinkLocal)
            {
                return;
            }
            throw new InvalidDataException(
                $"Peer {source.Ipv4Address}/{source.Ipv6Address} spoofed source {packet.Source}.");
        }

        RoutedPacketEndpoint? destination = FindRoute(packet.Destination);
        if (destination is not null)
        {
            await destination.EnqueueAsync(packetBytes, cancellationToken);
            return;
        }

        if (network.Contains(packet.Destination) &&
            !packet.Destination.Equals(serverIpv4) &&
            !packet.Destination.Equals(serverIpv6))
        {
            return;
        }

        await exitWriterLock.WaitAsync(cancellationToken);
        try
        {
            await exitNode.PacketWriter.WriteAsync(packetBytes, cancellationToken);
        }
        finally
        {
            exitWriterLock.Release();
        }
    }

    internal void Unregister(RoutedPacketEndpoint peer)
    {
        lock (routesLock)
        {
            RemoveIfOwned(peer.Ipv4Address, peer);
            RemoveIfOwned(peer.Ipv6Address, peer);
        }
    }

    RoutedPacketEndpoint? FindRoute(IPAddress address)
    {
        lock (routesLock)
            return routes.GetValueOrDefault(address);
    }

    void RemoveIfOwned(IPAddress address, RoutedPacketEndpoint peer)
    {
        if (routes.GetValueOrDefault(address) == peer)
            routes.Remove(address);
    }
}

public sealed class RoutedPacketEndpoint : IPacketEndpoint, IAsyncDisposable
{
    readonly TunnelPacketRouter router;
    readonly PacketChannelReadStream reader = new();
    readonly RoutedPacketWriteStream writer;
    int isDisposed;

    internal RoutedPacketEndpoint(TunnelPacketRouter router, IPAddress ipv4Address, IPAddress ipv6Address)
    {
        this.router = router;
        Ipv4Address = ipv4Address;
        Ipv6Address = ipv6Address;
        writer = new RoutedPacketWriteStream(this);
    }

    public IPAddress Ipv4Address { get; }
    public IPAddress Ipv6Address { get; }
    public Stream PacketReader => reader;
    public Stream PacketWriter => writer;

    internal bool Owns(IPAddress address) =>
        address.Equals(Ipv4Address) || address.Equals(Ipv6Address);

    internal async ValueTask EnqueueAsync(
        ReadOnlyMemory<byte> packet,
        CancellationToken cancellationToken)
    {
        if (isDisposed != 0)
            return;
        try
        {
            await reader.EnqueueAsync(packet.ToArray(), cancellationToken);
        }
        catch (ChannelClosedException)
        {
            // A concurrently disconnected destination simply drops the packet.
        }
    }

    public ValueTask InterruptReadAsync()
    {
        reader.Complete();
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref isDisposed, 1) == 0)
        {
            router.Unregister(this);
            reader.Dispose();
            writer.Dispose();
        }
        return ValueTask.CompletedTask;
    }

    sealed class RoutedPacketWriteStream(RoutedPacketEndpoint endpoint) : Stream
    {
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default) =>
            endpoint.router.RoutePeerPacketAsync(endpoint, buffer, cancellationToken);

        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    sealed class PacketChannelReadStream : Stream
    {
        readonly Channel<byte[]> packets = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(256)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait
        });

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
            try
            {
                byte[] packet = await packets.Reader.ReadAsync(cancellationToken);
                if (packet.Length > buffer.Length)
                    throw new InvalidDataException("The routed packet does not fit in the read buffer.");
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

        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}

internal readonly record struct PacketAddresses(IPAddress Source, IPAddress Destination)
{
    public static PacketAddresses Parse(ReadOnlySpan<byte> packet)
    {
        if (packet.IsEmpty)
            throw new InvalidDataException("Received an empty IP packet.");

        return (packet[0] >> 4) switch
        {
            4 when packet.Length >= 20 => new PacketAddresses(
                new IPAddress(packet.Slice(12, 4)),
                new IPAddress(packet.Slice(16, 4))),
            6 when packet.Length >= 40 => new PacketAddresses(
                new IPAddress(packet.Slice(8, 16)),
                new IPAddress(packet.Slice(24, 16))),
            4 => throw new InvalidDataException("Received a truncated IPv4 packet."),
            6 => throw new InvalidDataException("Received a truncated IPv6 packet."),
            _ => throw new InvalidDataException("Received a packet with an unsupported IP version.")
        };
    }
}
