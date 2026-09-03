using System.Runtime.CompilerServices;

namespace VpnSample.Protocol;

public sealed class PassThroughStage : ITunnelStage
{
    public async IAsyncEnumerable<TunnelFrame> OutboundAsync(
        IAsyncEnumerable<TunnelFrame> input,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (TunnelFrame frame in input.WithCancellation(cancellationToken))
            yield return frame;
    }

    public async IAsyncEnumerable<TunnelFrame> InboundAsync(
        IAsyncEnumerable<TunnelFrame> input,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (TunnelFrame frame in input.WithCancellation(cancellationToken))
            yield return frame;
    }
}
