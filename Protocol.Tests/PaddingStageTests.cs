using System.Runtime.CompilerServices;
using VpnSample.Protocol;

namespace VpnSample.Protocol.Tests;

public sealed class PaddingStageTests
{
    [Theory]
    [InlineData(20, 64)]
    [InlineData(100, 128)]
    [InlineData(240, 256)]
    public async Task PadsToBucketAndRestoresPayload(int payloadLength, int bucketSize)
    {
        var stage = new PaddingStage(64, 128, 256);
        byte[] payload = Enumerable.Range(0, payloadLength).Select(value => (byte)value).ToArray();
        TunnelFrame original = TunnelFrame.FromPacket(7, payload);

        TunnelFrame padded = Assert.Single(await ToListAsync(
            stage.OutboundAsync(Frames(original), CancellationToken.None)));
        Assert.Equal(bucketSize, padded.Payload.Length);

        TunnelFrame restored = Assert.Single(await ToListAsync(
            stage.InboundAsync(Frames(padded), CancellationToken.None)));
        Assert.Equal(payload, restored.Payload.ToArray());
    }

    [Fact]
    public async Task RejectsUnknownInboundBucket()
    {
        var stage = new PaddingStage(64, 128);
        TunnelFrame invalid = TunnelFrame.FromPacket(1, new byte[65]);

        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await ToListAsync(stage.InboundAsync(Frames(invalid), CancellationToken.None)));
    }

    static async IAsyncEnumerable<TunnelFrame> Frames(
        TunnelFrame frame,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await Task.Yield();
        yield return frame;
    }

    static async Task<List<TunnelFrame>> ToListAsync(IAsyncEnumerable<TunnelFrame> frames)
    {
        var result = new List<TunnelFrame>();
        await foreach (TunnelFrame frame in frames)
            result.Add(frame);
        return result;
    }
}
