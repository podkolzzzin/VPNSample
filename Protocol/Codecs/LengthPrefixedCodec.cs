using System.Buffers.Binary;

namespace VpnSample.Protocol;

public sealed class LengthPrefixedCodec : IWireCodec
{
    const int PrefixLength = sizeof(uint);
    const int MetadataLength = sizeof(ulong) + sizeof(ushort) + sizeof(ushort);
    const int HeaderLength = PrefixLength + MetadataLength;
    const int MaximumBodyLength = MetadataLength + TunnelFrame.MaximumPayloadLength;

    public async ValueTask WriteAsync(
        Stream transport,
        TunnelFrame frame,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentNullException.ThrowIfNull(frame);

        var header = new byte[HeaderLength];
        BinaryPrimitives.WriteUInt32BigEndian(
            header,
            checked((uint)(MetadataLength + frame.Payload.Length)));
        BinaryPrimitives.WriteUInt64BigEndian(header.AsSpan(PrefixLength), frame.PacketId);
        BinaryPrimitives.WriteUInt16BigEndian(
            header.AsSpan(PrefixLength + sizeof(ulong)),
            frame.FragmentIndex);
        BinaryPrimitives.WriteUInt16BigEndian(
            header.AsSpan(PrefixLength + sizeof(ulong) + sizeof(ushort)),
            frame.FragmentCount);

        await transport.WriteAsync(header, cancellationToken);
        await transport.WriteAsync(frame.Payload, cancellationToken);
    }

    public async ValueTask<TunnelFrame> ReadAsync(
        Stream transport,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(transport);

        var prefix = new byte[PrefixLength];
        await transport.ReadExactlyAsync(prefix, cancellationToken);
        uint encodedBodyLength = BinaryPrimitives.ReadUInt32BigEndian(prefix);
        if (encodedBodyLength is <= MetadataLength or > MaximumBodyLength)
            throw new InvalidDataException($"Invalid tunnel frame body length: {encodedBodyLength}.");

        var metadata = new byte[MetadataLength];
        await transport.ReadExactlyAsync(metadata, cancellationToken);

        ulong packetId = BinaryPrimitives.ReadUInt64BigEndian(metadata);
        ushort fragmentIndex = BinaryPrimitives.ReadUInt16BigEndian(metadata.AsSpan(sizeof(ulong)));
        ushort fragmentCount = BinaryPrimitives.ReadUInt16BigEndian(
            metadata.AsSpan(sizeof(ulong) + sizeof(ushort)));

        if (packetId == 0)
            throw new InvalidDataException("The tunnel frame has an invalid packet ID.");
        if (fragmentCount == 0 || fragmentIndex >= fragmentCount)
            throw new InvalidDataException("The tunnel frame has invalid fragment metadata.");

        int payloadLength = checked((int)encodedBodyLength - MetadataLength);
        var payload = new byte[payloadLength];
        await transport.ReadExactlyAsync(payload, cancellationToken);
        return new TunnelFrame(packetId, fragmentIndex, fragmentCount, payload);
    }
}
