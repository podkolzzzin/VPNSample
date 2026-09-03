using System.Runtime.CompilerServices;

namespace VpnSample.Protocol;

public sealed class FragmentStage : ITunnelStage
{
    readonly int maximumFragmentLength;

    public FragmentStage(int maximumFragmentLength)
    {
        if (maximumFragmentLength is < 1 or > TunnelFrame.MaximumPayloadLength)
            throw new ArgumentOutOfRangeException(nameof(maximumFragmentLength));

        this.maximumFragmentLength = maximumFragmentLength;
    }

    public IAsyncEnumerable<TunnelFrame> OutboundAsync(
        IAsyncEnumerable<TunnelFrame> input,
        CancellationToken cancellationToken) =>
        FragmentAsync(input, cancellationToken);

    public IAsyncEnumerable<TunnelFrame> InboundAsync(
        IAsyncEnumerable<TunnelFrame> input,
        CancellationToken cancellationToken) =>
        ReassembleAsync(input, cancellationToken);

    async IAsyncEnumerable<TunnelFrame> FragmentAsync(
        IAsyncEnumerable<TunnelFrame> input,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (TunnelFrame frame in input.WithCancellation(cancellationToken))
        {
            if (!frame.IsCompletePacket)
                throw new InvalidDataException("FragmentStage expects complete outbound packets.");

            if (frame.Payload.Length <= maximumFragmentLength)
            {
                yield return frame;
                continue;
            }

            int fragmentCount =
                (frame.Payload.Length + maximumFragmentLength - 1) / maximumFragmentLength;
            for (int index = 0; index < fragmentCount; index++)
            {
                int offset = index * maximumFragmentLength;
                int length = Math.Min(maximumFragmentLength, frame.Payload.Length - offset);
                yield return new TunnelFrame(
                    frame.PacketId,
                    checked((ushort)index),
                    checked((ushort)fragmentCount),
                    frame.Payload.Slice(offset, length));
            }
        }
    }

    async IAsyncEnumerable<TunnelFrame> ReassembleAsync(
        IAsyncEnumerable<TunnelFrame> input,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ulong packetId = 0;
        byte[][]? fragments = null;
        int receivedCount = 0;
        int payloadLength = 0;

        await foreach (TunnelFrame frame in input.WithCancellation(cancellationToken))
        {
            if (frame.IsCompletePacket)
            {
                if (fragments is not null)
                    throw new InvalidDataException($"Packet {packetId} ended before all fragments arrived.");

                yield return frame;
                continue;
            }

            if (frame.Payload.Length > maximumFragmentLength)
                throw new InvalidDataException(
                    $"Packet {frame.PacketId} contains an oversized fragment.");

            if (fragments is null)
            {
                int maximumFragmentCount =
                    (TunnelFrame.MaximumPayloadLength + maximumFragmentLength - 1) /
                    maximumFragmentLength;
                if (frame.FragmentCount > maximumFragmentCount)
                    throw new InvalidDataException(
                        $"Packet {frame.PacketId} declares too many fragments.");

                packetId = frame.PacketId;
                fragments = new byte[frame.FragmentCount][];
            }
            else if (frame.PacketId != packetId || frame.FragmentCount != fragments.Length)
            {
                throw new InvalidDataException($"Packet {packetId} has an incomplete fragment sequence.");
            }

            int index = frame.FragmentIndex;
            if (fragments[index] is not null)
                throw new InvalidDataException(
                    $"Packet {packetId} contains duplicate fragment {index}.");

            byte[] payload = frame.Payload.ToArray();
            fragments[index] = payload;
            receivedCount++;
            payloadLength = checked(payloadLength + payload.Length);
            if (payloadLength > TunnelFrame.MaximumPayloadLength)
                throw new InvalidDataException($"Packet {packetId} is too large after reassembly.");

            if (receivedCount != fragments.Length)
                continue;

            var packet = new byte[payloadLength];
            int destinationOffset = 0;
            foreach (byte[] fragment in fragments)
            {
                if (fragment is null)
                    throw new InvalidDataException($"Packet {packetId} is missing a fragment.");

                fragment.CopyTo(packet, destinationOffset);
                destinationOffset += fragment.Length;
            }

            yield return TunnelFrame.FromPacket(packetId, packet);
            packetId = 0;
            fragments = null;
            receivedCount = 0;
            payloadLength = 0;
        }

        if (fragments is not null)
            throw new InvalidDataException($"Packet {packetId} ended before all fragments arrived.");
    }
}
