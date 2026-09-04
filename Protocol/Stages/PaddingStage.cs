using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;

namespace VpnSample.Protocol;

public sealed class PaddingStage : ITunnelStage
{
    const int LengthPrefixSize = sizeof(ushort);
    readonly int[] bucketSizes;

    public PaddingStage(params int[] bucketSizes)
    {
        ArgumentNullException.ThrowIfNull(bucketSizes);
        if (bucketSizes.Length == 0 ||
            bucketSizes.Any(size => size <= LengthPrefixSize || size > TunnelFrame.MaximumPayloadLength) ||
            !bucketSizes.SequenceEqual(bucketSizes.Order()))
        {
            throw new ArgumentException(
                "Padding buckets must be sorted sizes between 3 and the maximum frame payload.",
                nameof(bucketSizes));
        }

        this.bucketSizes = bucketSizes.ToArray();
    }

    public IAsyncEnumerable<TunnelFrame> OutboundAsync(
        IAsyncEnumerable<TunnelFrame> input,
        CancellationToken cancellationToken) =>
        AddPaddingAsync(input, cancellationToken);

    public IAsyncEnumerable<TunnelFrame> InboundAsync(
        IAsyncEnumerable<TunnelFrame> input,
        CancellationToken cancellationToken) =>
        RemovePaddingAsync(input, cancellationToken);

    async IAsyncEnumerable<TunnelFrame> AddPaddingAsync(
        IAsyncEnumerable<TunnelFrame> input,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (TunnelFrame frame in input.WithCancellation(cancellationToken))
        {
            int encodedLength = checked(frame.Payload.Length + LengthPrefixSize);
            int bucketSize = bucketSizes.FirstOrDefault(size => size >= encodedLength);
            if (bucketSize == 0)
                throw new InvalidDataException(
                    $"No padding bucket can hold a {frame.Payload.Length}-byte frame.");

            var padded = new byte[bucketSize];
            BinaryPrimitives.WriteUInt16BigEndian(padded, checked((ushort)frame.Payload.Length));
            frame.Payload.CopyTo(padded.AsMemory(LengthPrefixSize));
            RandomNumberGenerator.Fill(padded.AsSpan(encodedLength));
            yield return CopyWithPayload(frame, padded);
        }
    }

    async IAsyncEnumerable<TunnelFrame> RemovePaddingAsync(
        IAsyncEnumerable<TunnelFrame> input,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (TunnelFrame frame in input.WithCancellation(cancellationToken))
        {
            if (!bucketSizes.Contains(frame.Payload.Length))
                throw new InvalidDataException("A padded frame does not use a configured bucket size.");

            int payloadLength = BinaryPrimitives.ReadUInt16BigEndian(frame.Payload.Span);
            if (payloadLength == 0 || payloadLength > frame.Payload.Length - LengthPrefixSize)
                throw new InvalidDataException("A padded frame contains an invalid payload length.");

            yield return CopyWithPayload(
                frame,
                frame.Payload.Slice(LengthPrefixSize, payloadLength).ToArray());
        }
    }

    static TunnelFrame CopyWithPayload(TunnelFrame frame, ReadOnlyMemory<byte> payload) =>
        new(frame.PacketId, frame.FragmentIndex, frame.FragmentCount, payload);
}
