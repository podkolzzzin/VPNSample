using VpnSample.Protocol;

namespace VpnSample.Protocol.Tests;

public sealed class FragmentStageTests
{
    [Fact]
    public async Task SplitsAndReassemblesOnePacket()
    {
        var stage = new FragmentStage(maximumFragmentLength: 256);
        byte[] packet = Enumerable.Range(0, 700).Select(value => (byte)value).ToArray();

        List<TunnelFrame> fragments = await ToListAsync(
            stage.OutboundAsync(Frames(TunnelFrame.FromPacket(7, packet)), CancellationToken.None));

        Assert.Equal(3, fragments.Count);
        Assert.Equal([256, 256, 188], fragments.Select(frame => frame.Payload.Length));
        Assert.Equal([0, 1, 2], fragments.Select(frame => (int)frame.FragmentIndex));
        Assert.All(fragments, frame => Assert.Equal((ushort)3, frame.FragmentCount));

        TunnelFrame restored = Assert.Single(await ToListAsync(
            stage.InboundAsync(Frames(fragments.ToArray()), CancellationToken.None)));
        Assert.True(restored.IsCompletePacket);
        Assert.Equal(packet, restored.Payload.ToArray());
    }

    [Fact]
    public async Task LeavesSmallPacketsComplete()
    {
        var stage = new FragmentStage(maximumFragmentLength: 256);
        TunnelFrame packet = TunnelFrame.FromPacket(1, new byte[64]);

        TunnelFrame result = Assert.Single(await ToListAsync(
            stage.OutboundAsync(Frames(packet), CancellationToken.None)));

        Assert.Same(packet, result);
    }

    static async IAsyncEnumerable<TunnelFrame> Frames(
        params TunnelFrame[] frames)
    {
        foreach (TunnelFrame frame in frames)
        {
            await Task.Yield();
            yield return frame;
        }
    }

    static async Task<List<TunnelFrame>> ToListAsync(IAsyncEnumerable<TunnelFrame> frames)
    {
        var result = new List<TunnelFrame>();
        await foreach (TunnelFrame frame in frames)
            result.Add(frame);
        return result;
    }
}
