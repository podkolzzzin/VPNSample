using System.Net;
using System.Net.Sockets;

namespace VpnSample.Protocol;

public sealed class TunnelNetwork
{
    public int DefaultPort { get; } = 443;
    public string DefaultTlsServerName { get; } = "vpn.twocubes.io";
    public int ClientCapacity { get; } = 253;
    public string ClientInterfaceName { get; } = "svpn0";
    public string ServerInterfaceName { get; } = "svpn0";
    public string Ipv4Prefix { get; } = "10.8.0";
    public int Ipv4NetworkPrefixLength { get; } = 24;
    public int Ipv4InterfacePrefixLength { get; } = 24;
    public string Ipv6Prefix { get; } = "fd42:8";
    public int Ipv6NetworkPrefixLength { get; } = 64;
    public int Ipv6InterfacePrefixLength { get; } = 64;
    public int OverlayMtu { get; } = 1280;
    public string Ipv4RouteProbe { get; } = "1.1.1.1";
    public string Ipv6RouteProbe { get; } = "2606:4700:4700::1111";
    public string Ipv4Network => $"{Ipv4Prefix}.0/{Ipv4NetworkPrefixLength}";
    public string Ipv6Network => $"{Ipv6Prefix}::/{Ipv6NetworkPrefixLength}";
    public string ServerIpv4 => $"{Ipv4Prefix}.1";
    public string ServerIpv6 => $"{Ipv6Prefix}::1";

    public TunnelAddresses GetAddresses(int clientNumber)
    {
        if (clientNumber < 0 || clientNumber >= ClientCapacity)
            throw new ArgumentOutOfRangeException(nameof(clientNumber));

        int hostNumber = clientNumber + 2;

        return new TunnelAddresses(
            ServerIpv4,
            ClientIpv4: $"{Ipv4Prefix}.{hostNumber}",
            ServerIpv6,
            ClientIpv6: $"{Ipv6Prefix}::{hostNumber:x}");
    }

    public bool Contains(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        return address.AddressFamily switch
        {
            AddressFamily.InterNetwork => PrefixMatches(
                address.GetAddressBytes(),
                IPAddress.Parse(ServerIpv4).GetAddressBytes(),
                Ipv4NetworkPrefixLength),
            AddressFamily.InterNetworkV6 => PrefixMatches(
                address.GetAddressBytes(),
                IPAddress.Parse(ServerIpv6).GetAddressBytes(),
                Ipv6NetworkPrefixLength),
            _ => false
        };
    }

    static bool PrefixMatches(byte[] address, byte[] network, int prefixLength)
    {
        int wholeBytes = prefixLength / 8;
        int remainingBits = prefixLength % 8;
        if (!address.AsSpan(0, wholeBytes).SequenceEqual(network.AsSpan(0, wholeBytes)))
            return false;
        if (remainingBits == 0)
            return true;

        int mask = 0xff << (8 - remainingBits);
        return (address[wholeBytes] & mask) == (network[wholeBytes] & mask);
    }
}

public sealed record TunnelAddresses(
    string ServerIpv4,
    string ClientIpv4,
    string ServerIpv6,
    string ClientIpv6);
