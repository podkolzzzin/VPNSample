using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using VpnSample.Dns;

namespace VpnSample.Dns.Tests;

public sealed class DnsMessageTests
{
    [Theory]
    [InlineData(1, "10.8.0.2")]
    [InlineData(28, "fd42:8::2")]
    public void AnswersAddressQueries(ushort recordType, string expectedAddress)
    {
        var registry = new OverlayDnsRegistry();
        using OverlayDnsRegistration registration = registry.TryRegister(
            "nginx-node",
            IPAddress.Parse("10.8.0.2"),
            IPAddress.Parse("fd42:8::2"))!;
        byte[] query = CreateQuery("nginx-node.vpn", recordType);

        byte[] response = DnsMessage.CreateResponse(query, registry)!;

        Assert.Equal(1, ReadUInt16(response, 6));
        int addressLength = ReadUInt16(response, query.Length + 10);
        Assert.Equal(
            IPAddress.Parse(expectedAddress),
            new IPAddress(response.AsSpan(query.Length + 12, addressLength)));
    }

    [Fact]
    public void ReturnsAuthoritativeNameErrorForUnknownNode()
    {
        byte[] response = DnsMessage.CreateResponse(
            CreateQuery("missing.vpn", 1),
            new OverlayDnsRegistry())!;

        Assert.Equal(3, ReadUInt16(response, 2) & 0xf);
        Assert.Equal(0, ReadUInt16(response, 6));
    }

    [Fact]
    public async Task ServesQueriesOverUdp()
    {
        var registry = new OverlayDnsRegistry();
        using OverlayDnsRegistration registration = registry.TryRegister(
            "nginx-node",
            IPAddress.Parse("10.8.0.2"),
            IPAddress.Parse("fd42:8::2"))!;
        await using var server = new OverlayDnsServer(
            new IPEndPoint(IPAddress.Loopback, 0),
            registry);
        using var serverStop = new CancellationTokenSource();
        Task serving = server.RunAsync(serverStop.Token);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var client = new UdpClient(AddressFamily.InterNetwork);

        byte[] query = CreateQuery("nginx-node.vpn", 1);
        await client.SendAsync(query, server.LocalEndpoint, timeout.Token);
        UdpReceiveResult result = await client.ReceiveAsync(timeout.Token);

        Assert.Equal(1, ReadUInt16(result.Buffer, 6));
        serverStop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => serving);
    }

    static byte[] CreateQuery(string name, ushort recordType)
    {
        var query = new List<byte>();
        WriteUInt16(query, 0x1234);
        WriteUInt16(query, 0x0100);
        WriteUInt16(query, 1);
        WriteUInt16(query, 0);
        WriteUInt16(query, 0);
        WriteUInt16(query, 0);
        foreach (string label in name.Split('.'))
        {
            byte[] bytes = Encoding.ASCII.GetBytes(label);
            query.Add(checked((byte)bytes.Length));
            query.AddRange(bytes);
        }
        query.Add(0);
        WriteUInt16(query, recordType);
        WriteUInt16(query, 1);
        return query.ToArray();
    }

    static ushort ReadUInt16(byte[] bytes, int offset) =>
        BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(offset));

    static void WriteUInt16(List<byte> bytes, ushort value)
    {
        bytes.Add((byte)(value >> 8));
        bytes.Add((byte)value);
    }
}
