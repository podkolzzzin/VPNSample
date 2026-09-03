namespace VpnSample.Protocol;

public sealed class TunnelNetwork
{
    public int DefaultPort { get; } = 4433;
    public int ClientCapacity { get; } = 256;
    public string ClientInterfaceName { get; } = "svpn0";
    public string ServerInterfacePrefix { get; } = "svpn";
    public string Ipv4Prefix { get; } = "10.8";
    public int Ipv4NetworkPrefixLength { get; } = 16;
    public int Ipv4InterfacePrefixLength { get; } = 32;
    public string Ipv6Prefix { get; } = "fd42:8";
    public int Ipv6NetworkPrefixLength { get; } = 48;
    public int Ipv6InterfacePrefixLength { get; } = 64;
    public string Ipv4RouteProbe { get; } = "1.1.1.1";
    public string Ipv6RouteProbe { get; } = "2606:4700:4700::1111";
    public string Ipv4Network => $"{Ipv4Prefix}.0.0/{Ipv4NetworkPrefixLength}";
    public string Ipv6Network => $"{Ipv6Prefix}::/{Ipv6NetworkPrefixLength}";

    public TunnelAddresses GetAddresses(int clientNumber)
    {
        if (clientNumber < 0 || clientNumber >= ClientCapacity)
            throw new ArgumentOutOfRangeException(nameof(clientNumber));

        string ipv6Subnet = clientNumber == 0
            ? Ipv6Prefix
            : $"{Ipv6Prefix}:{clientNumber:x}";

        return new TunnelAddresses(
            ServerIpv4: $"{Ipv4Prefix}.{clientNumber}.1",
            ClientIpv4: $"{Ipv4Prefix}.{clientNumber}.2",
            ServerIpv6: $"{ipv6Subnet}::1",
            ClientIpv6: $"{ipv6Subnet}::2");
    }

    public string GetServerInterfaceName(int clientNumber) =>
        $"{ServerInterfacePrefix}{clientNumber}";
}

public sealed record TunnelAddresses(
    string ServerIpv4,
    string ClientIpv4,
    string ServerIpv6,
    string ClientIpv6);
