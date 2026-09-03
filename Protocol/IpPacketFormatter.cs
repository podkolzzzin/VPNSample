using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;

namespace VpnSample.Protocol;

static class IpPacketFormatter
{
    const int Ipv4MinimumHeaderLength = 20;
    const int Ipv6HeaderLength = 40;

    public static string Format(ReadOnlySpan<byte> packet)
    {
        if (packet.IsEmpty)
            return "unknown";

        return (packet[0] >> 4) switch
        {
            4 => FormatIpv4(packet),
            6 => FormatIpv6(packet),
            var version => $"IPv{version} malformed"
        };
    }

    static string FormatIpv4(ReadOnlySpan<byte> packet)
    {
        if (packet.Length < Ipv4MinimumHeaderLength)
            return "IPv4 malformed";

        int headerLength = (packet[0] & 0x0f) * 4;
        if (headerLength < Ipv4MinimumHeaderLength || packet.Length < headerLength)
            return "IPv4 malformed";

        var source = new IPAddress(packet.Slice(12, 4));
        var destination = new IPAddress(packet.Slice(16, 4));
        byte protocol = packet[9];
        int fragmentOffset = BinaryPrimitives.ReadUInt16BigEndian(packet.Slice(6, 2)) & 0x1fff;

        return FormatEndpoints(protocol, source, destination,
            packet[headerLength..], fragmentOffset == 0);
    }

    static string FormatIpv6(ReadOnlySpan<byte> packet)
    {
        if (packet.Length < Ipv6HeaderLength)
            return "IPv6 malformed";

        var source = new IPAddress(packet.Slice(8, 16));
        var destination = new IPAddress(packet.Slice(24, 16));
        byte nextHeader = packet[6];
        int offset = Ipv6HeaderLength;
        bool hasTransportHeader = true;

        while (true)
        {
            int extensionLength;
            switch (nextHeader)
            {
                case 0:  // Hop-by-Hop Options
                case 43: // Routing
                case 60: // Destination Options
                    if (packet.Length < offset + 2)
                        return "IPv6 malformed";
                    extensionLength = (packet[offset + 1] + 1) * 8;
                    break;

                case 44: // Fragment
                    if (packet.Length < offset + 8)
                        return "IPv6 malformed";
                    hasTransportHeader = (BinaryPrimitives.ReadUInt16BigEndian(
                        packet.Slice(offset + 2, 2)) & 0xfff8) == 0;
                    extensionLength = 8;
                    break;

                case 51: // Authentication Header
                    if (packet.Length < offset + 2)
                        return "IPv6 malformed";
                    extensionLength = (packet[offset + 1] + 2) * 4;
                    break;

                default:
                    return FormatEndpoints(nextHeader, source, destination,
                        packet[offset..], hasTransportHeader);
            }

            if (packet.Length < offset + extensionLength)
                return "IPv6 malformed";

            nextHeader = packet[offset];
            offset += extensionLength;
        }
    }

    static string FormatEndpoints(
        byte protocol,
        IPAddress source,
        IPAddress destination,
        ReadOnlySpan<byte> transport,
        bool hasTransportHeader)
    {
        string protocolName = protocol switch
        {
            1 => "ICMP",
            6 => "TCP",
            17 => "UDP",
            47 => "GRE",
            50 => "ESP",
            58 => "ICMPv6",
            _ => $"IP/{protocol}"
        };

        if (hasTransportHeader && protocol is 6 or 17 && transport.Length >= 4)
        {
            ushort sourcePort = BinaryPrimitives.ReadUInt16BigEndian(transport);
            ushort destinationPort = BinaryPrimitives.ReadUInt16BigEndian(transport[2..]);
            return $"{protocolName,-6} {FormatEndpoint(source, sourcePort)} → " +
                FormatEndpoint(destination, destinationPort);
        }

        return $"{protocolName,-6} {source} → {destination}";
    }

    static string FormatEndpoint(IPAddress address, ushort port) =>
        address.AddressFamily == AddressFamily.InterNetworkV6
            ? $"[{address}]:{port}"
            : $"{address}:{port}";
}
