using System.Buffers.Binary;
using VpnSample.Protocol;

namespace VpnSample.Protocol.Tests;

public sealed class LengthPrefixedCodecTests
{
    [Fact]
    public async Task RoundTripsFrameAndFragmentMetadata()
    {
        var expected = new TunnelFrame(123, 2, 4, new byte[] { 10, 20, 30, 40 });
        var transport = new MemoryStream();
        var codec = new LengthPrefixedCodec();

        await codec.WriteAsync(transport, expected, CancellationToken.None);
        transport.Position = 0;
        TunnelFrame actual = await codec.ReadAsync(transport, CancellationToken.None);

        Assert.Equal(expected.PacketId, actual.PacketId);
        Assert.Equal(expected.FragmentIndex, actual.FragmentIndex);
        Assert.Equal(expected.FragmentCount, actual.FragmentCount);
        Assert.Equal(expected.Payload.ToArray(), actual.Payload.ToArray());
    }

    [Fact]
    public async Task RejectsBodyWithoutPayload()
    {
        var invalidFrame = new byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32BigEndian(invalidFrame, 12);
        var transport = new MemoryStream(invalidFrame);
        var codec = new LengthPrefixedCodec();

        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await codec.ReadAsync(transport, CancellationToken.None));
    }
}
