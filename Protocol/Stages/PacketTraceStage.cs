using System.Runtime.CompilerServices;

namespace VpnSample.Protocol;

public sealed class PacketTraceStage : ITunnelStage, IDisposable
{
    readonly PacketTrace trace;

    public PacketTraceStage(string traceSide)
    {
        trace = new PacketTrace(traceSide);
    }

    public async IAsyncEnumerable<TunnelFrame> OutboundAsync(
        IAsyncEnumerable<TunnelFrame> input,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (TunnelFrame frame in input.WithCancellation(cancellationToken))
        {
            trace.Write(PacketFlow.Send, frame.Payload.Span);
            yield return frame;
        }
    }

    public async IAsyncEnumerable<TunnelFrame> InboundAsync(
        IAsyncEnumerable<TunnelFrame> input,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (TunnelFrame frame in input.WithCancellation(cancellationToken))
        {
            trace.Write(PacketFlow.Receive, frame.Payload.Span);
            yield return frame;
        }
    }

    public void Dispose() => trace.Dispose();
}
