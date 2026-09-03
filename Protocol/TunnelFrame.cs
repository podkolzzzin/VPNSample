namespace VpnSample.Protocol;

public sealed record TunnelFrame
{
    public const int MaximumPayloadLength = ushort.MaxValue;

    public TunnelFrame(
        ulong packetId,
        ushort fragmentIndex,
        ushort fragmentCount,
        ReadOnlyMemory<byte> payload)
    {
        if (packetId == 0)
            throw new ArgumentOutOfRangeException(nameof(packetId), "A packet ID must be non-zero.");
        if (fragmentCount == 0)
            throw new ArgumentOutOfRangeException(nameof(fragmentCount), "A frame must have at least one fragment.");
        if (fragmentIndex >= fragmentCount)
            throw new ArgumentOutOfRangeException(nameof(fragmentIndex), "The fragment index must be smaller than the fragment count.");
        if (payload.IsEmpty)
            throw new ArgumentException("A tunnel frame cannot have an empty payload.", nameof(payload));
        if (payload.Length > MaximumPayloadLength)
            throw new ArgumentException($"A tunnel frame payload cannot exceed {MaximumPayloadLength} bytes.", nameof(payload));

        PacketId = packetId;
        FragmentIndex = fragmentIndex;
        FragmentCount = fragmentCount;
        Payload = payload;
    }

    public ulong PacketId { get; }
    public ushort FragmentIndex { get; }
    public ushort FragmentCount { get; }
    public ReadOnlyMemory<byte> Payload { get; }
    public bool IsCompletePacket => FragmentIndex == 0 && FragmentCount == 1;

    public static TunnelFrame FromPacket(ulong packetId, ReadOnlyMemory<byte> packet) =>
        new(packetId, 0, 1, packet);
}
