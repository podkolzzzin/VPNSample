namespace VpnSample.Protocol;

public interface ITunnelStage
{
    IAsyncEnumerable<TunnelFrame> OutboundAsync(
        IAsyncEnumerable<TunnelFrame> input,
        CancellationToken cancellationToken);

    IAsyncEnumerable<TunnelFrame> InboundAsync(
        IAsyncEnumerable<TunnelFrame> input,
        CancellationToken cancellationToken);
}
