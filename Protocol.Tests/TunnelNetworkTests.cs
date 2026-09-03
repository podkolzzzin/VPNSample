using System.Net;
using VpnSample.Protocol;

namespace VpnSample.Protocol.Tests;

public sealed class TunnelNetworkTests
{
    [Fact]
    public void AssignsEveryClientToTheSameOverlayNetworks()
    {
        var network = new TunnelNetwork();
        var ipv4Addresses = new HashSet<string>();
        var ipv6Addresses = new HashSet<string>();

        for (int clientNumber = 0; clientNumber < network.ClientCapacity; clientNumber++)
        {
            TunnelAddresses addresses = network.GetAddresses(clientNumber);

            Assert.Equal(network.ServerIpv4, addresses.ServerIpv4);
            Assert.Equal(network.ServerIpv6, addresses.ServerIpv6);
            Assert.True(network.Contains(IPAddress.Parse(addresses.ClientIpv4)));
            Assert.True(network.Contains(IPAddress.Parse(addresses.ClientIpv6)));
            Assert.True(ipv4Addresses.Add(addresses.ClientIpv4));
            Assert.True(ipv6Addresses.Add(addresses.ClientIpv6));
        }

        Assert.Equal("10.8.0.0/24", network.Ipv4Network);
        Assert.Equal("fd42:8::/64", network.Ipv6Network);
        Assert.Equal("10.8.0.2", network.GetAddresses(0).ClientIpv4);
        Assert.Equal("10.8.0.254", network.GetAddresses(252).ClientIpv4);
    }

    [Fact]
    public void RejectsClientNumbersOutsideThePool()
    {
        var network = new TunnelNetwork();

        Assert.Throws<ArgumentOutOfRangeException>(() => network.GetAddresses(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => network.GetAddresses(network.ClientCapacity));
    }
}
