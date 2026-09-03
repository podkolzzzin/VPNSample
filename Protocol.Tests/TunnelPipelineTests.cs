using System.Runtime.CompilerServices;
using System.Text;
using VpnSample.Protocol;

namespace VpnSample.Protocol.Tests;

public sealed class TunnelPipelineTests
{
    [Fact]
    public async Task AppliesOutboundInRegistrationOrderAndInboundInReverseOrder()
    {
        await using TunnelPipeline pipeline = new TunnelPipelineBuilder("test", new StubCodec())
            .Use(new MarkerStage("A"))
            .Use(new MarkerStage("B"))
            .Build();

        TunnelFrame outbound = await SingleAsync(
            pipeline.ApplyOutboundStages(SingleFrame("packet"), CancellationToken.None));
        TunnelFrame inbound = await SingleAsync(
            pipeline.ApplyInboundStages(SingleFrame("packet"), CancellationToken.None));

        Assert.Equal("packet:out-A:out-B", Text(outbound));
        Assert.Equal("packet:in-B:in-A", Text(inbound));
    }

    [Fact]
    public void BuilderCanBuildOnlyOnce()
    {
        var builder = new TunnelPipelineBuilder("test", new StubCodec());
        _ = builder.Build();

        Assert.Throws<InvalidOperationException>(() => builder.Build());
    }

    [Fact]
    public void ProfileFactoryRejectsUnknownProfile()
    {
        Assert.False(TunnelProfileFactory.IsSupported("unknown"));
        Assert.Throws<ArgumentException>(() => TunnelProfileFactory.Create("unknown", "test"));
    }

    static async IAsyncEnumerable<TunnelFrame> SingleFrame(
        string text,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await Task.Yield();
        yield return TunnelFrame.FromPacket(1, Encoding.UTF8.GetBytes(text));
    }

    static async Task<TunnelFrame> SingleAsync(IAsyncEnumerable<TunnelFrame> frames)
    {
        await using IAsyncEnumerator<TunnelFrame> enumerator = frames.GetAsyncEnumerator();
        Assert.True(await enumerator.MoveNextAsync());
        TunnelFrame result = enumerator.Current;
        Assert.False(await enumerator.MoveNextAsync());
        return result;
    }

    static string Text(TunnelFrame frame) => Encoding.UTF8.GetString(frame.Payload.Span);

    sealed class MarkerStage(string name) : ITunnelStage
    {
        public IAsyncEnumerable<TunnelFrame> OutboundAsync(
            IAsyncEnumerable<TunnelFrame> input,
            CancellationToken cancellationToken) =>
            AppendAsync(input, $":out-{name}", cancellationToken);

        public IAsyncEnumerable<TunnelFrame> InboundAsync(
            IAsyncEnumerable<TunnelFrame> input,
            CancellationToken cancellationToken) =>
            AppendAsync(input, $":in-{name}", cancellationToken);

        static async IAsyncEnumerable<TunnelFrame> AppendAsync(
            IAsyncEnumerable<TunnelFrame> input,
            string marker,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await foreach (TunnelFrame frame in input.WithCancellation(cancellationToken))
            {
                byte[] payload = Encoding.UTF8.GetBytes(Text(frame) + marker);
                yield return new TunnelFrame(
                    frame.PacketId,
                    frame.FragmentIndex,
                    frame.FragmentCount,
                    payload);
            }
        }
    }

    sealed class StubCodec : IWireCodec
    {
        public ValueTask WriteAsync(
            Stream transport,
            TunnelFrame frame,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask<TunnelFrame> ReadAsync(
            Stream transport,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
