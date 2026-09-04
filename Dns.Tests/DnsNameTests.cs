using VpnSample.Dns;

namespace VpnSample.Dns.Tests;

public sealed class DnsNameTests
{
    [Theory]
    [InlineData("Web-One", "web-one")]
    [InlineData("node7", "node7")]
    public void NormalizesNodeLabels(string input, string expected)
    {
        Assert.Equal(expected, DnsName.NormalizeNodeName(input));
        Assert.Equal($"{expected}.vpn", DnsName.GetFullName(input));
    }

    [Theory]
    [InlineData("")]
    [InlineData("-node")]
    [InlineData("node-")]
    [InlineData("two.nodes")]
    [InlineData("not_valid")]
    public void RejectsInvalidNodeLabels(string nodeName)
    {
        Assert.ThrowsAny<ArgumentException>(() => DnsName.NormalizeNodeName(nodeName));
    }
}
