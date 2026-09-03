namespace VpnSample.Protocol;

public interface IWireCodec
{
    ValueTask WriteAsync(
        Stream transport,
        TunnelFrame frame,
        CancellationToken cancellationToken);

    ValueTask<TunnelFrame> ReadAsync(
        Stream transport,
        CancellationToken cancellationToken);
}
