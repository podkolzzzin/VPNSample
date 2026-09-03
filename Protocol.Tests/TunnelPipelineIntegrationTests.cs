using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;
using VpnSample.Protocol;

namespace VpnSample.Protocol.Tests;

public sealed class TunnelPipelineIntegrationTests
{
    [Fact]
    public async Task BaselineTransfersPacketsInBothDirections()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();

        var client = new TcpClient();
        Task<TcpClient> accept = listener.AcceptTcpClientAsync();
        await client.ConnectAsync((IPEndPoint)listener.LocalEndpoint);
        using TcpClient server = await accept;
        listener.Stop();

        using (client)
        using (var clientPackets = new TestPacketEndpoint())
        using (var serverPackets = new TestPacketEndpoint())
        await using (TunnelPipeline clientPipeline = TunnelProfileFactory.CreateBaseline("test"))
        await using (TunnelPipeline serverPipeline = TunnelProfileFactory.CreateBaseline("test"))
        using (var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
        {
            Task clientRun = clientPipeline.RunAsync(clientPackets, client.GetStream(), stop.Token);
            Task serverRun = serverPipeline.RunAsync(serverPackets, server.GetStream(), stop.Token);

            byte[] request = [0x45, 0x00, 0x00, 0x14];
            byte[] response = [0x60, 0x00, 0x00, 0x00];
            await clientPackets.EnqueueAsync(request, stop.Token);
            await serverPackets.EnqueueAsync(response, stop.Token);

            Assert.Equal(request, await serverPackets.ReadWrittenPacketAsync(stop.Token));
            Assert.Equal(response, await clientPackets.ReadWrittenPacketAsync(stop.Token));

            stop.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                Task.WhenAll(clientRun, serverRun));
        }
    }

    [Fact]
    public async Task ShuffleSplitTransfersSeveralLargePacketsInBothDirections()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();

        var client = new TcpClient();
        Task<TcpClient> accept = listener.AcceptTcpClientAsync();
        await client.ConnectAsync((IPEndPoint)listener.LocalEndpoint);
        using TcpClient server = await accept;
        listener.Stop();

        using (client)
        using (var clientPackets = new TestPacketEndpoint())
        using (var serverPackets = new TestPacketEndpoint())
        await using (TunnelPipeline clientPipeline = TunnelProfileFactory.CreateShuffleSplit("test"))
        await using (TunnelPipeline serverPipeline = TunnelProfileFactory.CreateShuffleSplit("test"))
        using (var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
        {
            Task clientRun = clientPipeline.RunAsync(clientPackets, client.GetStream(), stop.Token);
            Task serverRun = serverPipeline.RunAsync(serverPackets, server.GetStream(), stop.Token);
            byte[][] requests = Packets(0x40);
            byte[][] responses = Packets(0x60);

            foreach (byte[] packet in requests)
                await clientPackets.EnqueueAsync(packet, stop.Token);
            foreach (byte[] packet in responses)
                await serverPackets.EnqueueAsync(packet, stop.Token);

            byte[][] receivedRequests = await ReadPacketsAsync(serverPackets, stop.Token);
            byte[][] receivedResponses = await ReadPacketsAsync(clientPackets, stop.Token);
            Assert.Equal(SortedHex(requests), SortedHex(receivedRequests));
            Assert.Equal(SortedHex(responses), SortedHex(receivedResponses));

            stop.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                Task.WhenAll(clientRun, serverRun));
        }
    }

    static byte[][] Packets(byte marker) =>
        Enumerable.Range(0, 3)
            .Select(index => Enumerable.Repeat((byte)(marker + index), 900 + index).ToArray())
            .ToArray();

    static async Task<byte[][]> ReadPacketsAsync(
        TestPacketEndpoint endpoint,
        CancellationToken cancellationToken)
    {
        var packets = new byte[3][];
        for (int index = 0; index < packets.Length; index++)
            packets[index] = await endpoint.ReadWrittenPacketAsync(cancellationToken);
        return packets;
    }

    static string[] SortedHex(IEnumerable<byte[]> packets) =>
        packets.Select(Convert.ToHexString).Order().ToArray();

    sealed class TestPacketEndpoint : IPacketEndpoint, IDisposable
    {
        readonly PacketReadStream reader = new();
        readonly PacketWriteStream writer = new();

        public Stream PacketReader => reader;
        public Stream PacketWriter => writer;

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

    sealed class PacketReadStream : Stream
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
            try
            {
                byte[] packet = await packets.Reader.ReadAsync(cancellationToken);
                if (packet.Length > buffer.Length)
                    throw new InvalidOperationException("The test packet does not fit in the read buffer.");
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
        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();
    }

    sealed class PacketWriteStream : Stream
    {
        readonly Channel<byte[]> packets = Channel.CreateUnbounded<byte[]>();

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
        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();
    }
}
