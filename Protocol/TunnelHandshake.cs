using System.Buffers.Binary;
using System.Text;

namespace VpnSample.Protocol;

static class TunnelHandshake
{
    const ushort ProtocolVersion = 3;
    const int MaximumProfileNameLength = 64;
    static ReadOnlySpan<byte> Magic => "SVPN"u8;

    public static async Task NegotiateAsync(
        Stream transport,
        string profileName,
        CancellationToken cancellationToken)
    {
        byte[] localHello = EncodeHello(profileName);
        await transport.WriteAsync(localHello, cancellationToken);
        await transport.FlushAsync(cancellationToken);

        var header = new byte[Magic.Length + sizeof(ushort) + sizeof(byte)];
        await transport.ReadExactlyAsync(header, cancellationToken);

        if (!header.AsSpan(0, Magic.Length).SequenceEqual(Magic))
            throw new InvalidDataException("The peer did not send a VPN tunnel handshake.");

        ushort peerVersion = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(Magic.Length));
        if (peerVersion != ProtocolVersion)
            throw new InvalidDataException(
                $"Tunnel protocol version mismatch: local={ProtocolVersion}, peer={peerVersion}.");

        int profileLength = header[^1];
        if (profileLength == 0 || profileLength > MaximumProfileNameLength)
            throw new InvalidDataException("The peer sent an invalid tunnel profile name length.");

        var encodedProfile = new byte[profileLength];
        await transport.ReadExactlyAsync(encodedProfile, cancellationToken);
        string peerProfile = Encoding.UTF8.GetString(encodedProfile);
        if (!StringComparer.Ordinal.Equals(profileName, peerProfile))
            throw new InvalidDataException(
                $"Tunnel profile mismatch: local='{profileName}', peer='{peerProfile}'.");
    }

    internal static byte[] EncodeHello(string profileName)
    {
        byte[] encodedProfile = Encoding.UTF8.GetBytes(profileName);
        if (encodedProfile.Length is 0 or > MaximumProfileNameLength)
            throw new ArgumentException(
                $"A tunnel profile name must contain 1-{MaximumProfileNameLength} UTF-8 bytes.",
                nameof(profileName));

        var hello = new byte[Magic.Length + sizeof(ushort) + sizeof(byte) + encodedProfile.Length];
        Magic.CopyTo(hello);
        BinaryPrimitives.WriteUInt16BigEndian(hello.AsSpan(Magic.Length), ProtocolVersion);
        hello[Magic.Length + sizeof(ushort)] = checked((byte)encodedProfile.Length);
        encodedProfile.CopyTo(hello.AsSpan(Magic.Length + sizeof(ushort) + sizeof(byte)));
        return hello;
    }
}
