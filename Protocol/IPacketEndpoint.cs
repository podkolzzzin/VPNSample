namespace VpnSample.Protocol;

public interface IPacketEndpoint
{
    Stream PacketReader { get; }
    Stream PacketWriter { get; }

    ValueTask InterruptReadAsync();
}
