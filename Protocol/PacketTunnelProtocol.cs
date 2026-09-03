using System.Buffers.Binary;

namespace VpnSample.Protocol;

public sealed class PacketTunnelProtocol
{
    const int MaximumPacketLength = ushort.MaxValue;
    readonly string traceSide;

    public PacketTunnelProtocol(string traceSide = "peer")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(traceSide);
        this.traceSide = traceSide;
    }

    public async Task RunAsync(
        IPacketEndpoint packets,
        Stream transport,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(packets);
        ArgumentNullException.ThrowIfNull(transport);

        using var trace = new PacketTrace(traceSide);
        using var sessionStop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var send = SendPacketsAsync(packets.PacketReader, transport, trace, sessionStop.Token);
        var receive = ReceivePacketsAsync(transport, packets.PacketWriter, trace, sessionStop.Token);
        var completed = await Task.WhenAny(send, receive);
        sessionStop.Cancel();

        if (completed == receive)
            await packets.InterruptReadAsync();

        try
        {
            await Task.WhenAll(send, receive);
        }
        catch (OperationCanceledException) when (completed.IsCompletedSuccessfully)
        {
            // The unfinished direction was canceled after its peer completed normally.
        }

        await completed;
    }

    async Task SendPacketsAsync(
        Stream packetReader,
        Stream transport,
        PacketTrace trace,
        CancellationToken cancellationToken)
    {
        var packet = new byte[MaximumPacketLength];
        var header = new byte[sizeof(ushort)];

        while (!cancellationToken.IsCancellationRequested)
        {
            var length = await packetReader.ReadAsync(packet, cancellationToken);
            if (length == 0)
                throw new EndOfStreamException("The packet endpoint closed.");

            trace.Write(PacketFlow.Send, packet.AsSpan(0, length));
            BinaryPrimitives.WriteUInt16BigEndian(header, checked((ushort)length));
            await transport.WriteAsync(header, cancellationToken);
            await transport.WriteAsync(packet.AsMemory(0, length), cancellationToken);
        }
    }

    async Task ReceivePacketsAsync(
        Stream transport,
        Stream packetWriter,
        PacketTrace trace,
        CancellationToken cancellationToken)
    {
        var header = new byte[sizeof(ushort)];
        var packet = new byte[MaximumPacketLength];

        while (!cancellationToken.IsCancellationRequested)
        {
            await transport.ReadExactlyAsync(header, cancellationToken);
            var length = BinaryPrimitives.ReadUInt16BigEndian(header);
            if (length == 0)
                throw new InvalidDataException("The protocol received an empty packet frame.");

            await transport.ReadExactlyAsync(packet.AsMemory(0, length), cancellationToken);
            trace.Write(PacketFlow.Receive, packet.AsSpan(0, length));
            await packetWriter.WriteAsync(packet.AsMemory(0, length), cancellationToken);
        }
    }
}
