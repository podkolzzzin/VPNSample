using System.Runtime.CompilerServices;
using VpnSample.Protocol;

namespace VpnSample.Protocol.Tests;

public sealed class PacketShuffleStageTests
{
    [Fact]
    public async Task ReordersEachFullOutboundWindow()
    {
        var stage = new PacketShuffleStage(
            windowSize: 3,
            TimeSpan.FromSeconds(1),
            _ => 0);

        List<TunnelFrame> output = await ToListAsync(stage.OutboundAsync(
            Frames(Packet(1), Packet(2), Packet(3)),
            CancellationToken.None));

        Assert.Equal([2UL, 3UL, 1UL], output.Select(frame => frame.PacketId));
    }

    [Fact]
    public async Task FlushesAndReordersAPartialWindowAtEndOfStream()
    {
        var stage = new PacketShuffleStage(
            windowSize: 3,
            TimeSpan.FromSeconds(1),
            _ => 0);

        List<TunnelFrame> output = await ToListAsync(stage.OutboundAsync(
            Frames(Packet(1), Packet(2)),
            CancellationToken.None));

        Assert.Equal([2UL, 1UL], output.Select(frame => frame.PacketId));
    }

    [Fact]
    public async Task FlushesSparseTrafficWithoutWaitingForTheNextPacket()
    {
        var stage = new PacketShuffleStage(
            windowSize: 3,
            TimeSpan.FromMilliseconds(10),
            _ => 0);
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        await using IAsyncEnumerator<TunnelFrame> output = stage.OutboundAsync(
            SparseFrames(),
            CancellationToken.None).GetAsyncEnumerator();

        Assert.True(await output.MoveNextAsync());
        Assert.Equal(1UL, output.Current.PacketId);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromMilliseconds(500));
        Assert.True(await output.MoveNextAsync());
        Assert.Equal(2UL, output.Current.PacketId);
        Assert.False(await output.MoveNextAsync());
    }

    static TunnelFrame Packet(ulong packetId) =>
        TunnelFrame.FromPacket(packetId, new byte[] { checked((byte)packetId) });

    static async IAsyncEnumerable<TunnelFrame> Frames(
        TunnelFrame first,
        TunnelFrame second,
        TunnelFrame? third = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        foreach (TunnelFrame frame in new[] { first, second, third }.OfType<TunnelFrame>())
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Yield();
            yield return frame;
        }
    }

    static async IAsyncEnumerable<TunnelFrame> SparseFrames(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        yield return Packet(1);
        await Task.Delay(TimeSpan.FromMilliseconds(750), cancellationToken);
        yield return Packet(2);
    }

    static async Task<List<TunnelFrame>> ToListAsync(IAsyncEnumerable<TunnelFrame> frames)
    {
        var result = new List<TunnelFrame>();
        await foreach (TunnelFrame frame in frames)
            result.Add(frame);
        return result;
    }
}
