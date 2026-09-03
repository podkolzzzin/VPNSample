using VpnSample.Protocol;

namespace VpnSample.Protocol.Tests;

public sealed class TunnelFrameTests
{
    [Fact]
    public void FromPacketCreatesCompletePacket()
    {
        var payload = new byte[] { 1, 2, 3 };

        TunnelFrame frame = TunnelFrame.FromPacket(42, payload);

        Assert.Equal((ulong)42, frame.PacketId);
        Assert.Equal((ushort)0, frame.FragmentIndex);
        Assert.Equal((ushort)1, frame.FragmentCount);
        Assert.True(frame.IsCompletePacket);
        Assert.Equal(payload, frame.Payload.ToArray());
    }

    [Theory]
    [InlineData(0, 0, 1)]
    [InlineData(1, 1, 1)]
    [InlineData(1, 0, 0)]
    public void RejectsInvalidMetadata(ulong packetId, ushort fragmentIndex, ushort fragmentCount)
    {
        Assert.ThrowsAny<ArgumentException>(() =>
            new TunnelFrame(packetId, fragmentIndex, fragmentCount, new byte[] { 1 }));
    }
}
