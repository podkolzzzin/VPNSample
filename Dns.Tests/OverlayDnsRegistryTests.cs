using System.Net;
using VpnSample.Dns;

namespace VpnSample.Dns.Tests;

public sealed class OverlayDnsRegistryTests
{
    [Fact]
    public void HoldsBothAddressesForLeaseLifetime()
    {
        var registry = new OverlayDnsRegistry();
        using OverlayDnsRegistration registration = registry.TryRegister(
            "nginx-node",
            IPAddress.Parse("10.8.0.2"),
            IPAddress.Parse("fd42:8::2"))!;

        Assert.True(registry.TryResolve("NGINX-NODE.VPN.", out OverlayDnsRecord? record));
        Assert.Equal(IPAddress.Parse("10.8.0.2"), record!.Ipv4Address);
        Assert.Equal(IPAddress.Parse("fd42:8::2"), record.Ipv6Address);

        Assert.Null(registry.TryRegister(
            "nginx-node",
            IPAddress.Parse("10.8.0.3"),
            IPAddress.Parse("fd42:8::3")));

        registration.Dispose();
        Assert.False(registry.TryResolve("nginx-node.vpn", out _));
    }
}
