using VpnSample.Mesh;

namespace VpnSample.Mesh.Tests;

public sealed class MeshControlProtocolTests
{
    [Fact]
    public async Task RoundTripsRegistrationAndSnapshot()
    {
        var registration = new MeshRegistrationRequest(
            "alice",
            Convert.ToBase64String(new byte[91]),
            ["192.0.2.10:30000"]);
        await using var registrationStream = new MemoryStream();
        await MeshControlProtocol.WriteRegistrationAsync(registrationStream, registration);
        registrationStream.Position = 0;
        MeshRegistrationRequest decodedRegistration =
            await MeshControlProtocol.ReadRegistrationAsync(registrationStream);
        Assert.Equal(registration.NodeName, decodedRegistration.NodeName);
        Assert.Equal(registration.PublicKey, decodedRegistration.PublicKey);
        Assert.Equal(registration.LocalEndpoints, decodedRegistration.LocalEndpoints);

        var snapshot = new MeshSnapshot([
            new MeshPeerDescriptor(
                "alice", "10.8.0.2", "fd42:8::2", registration.PublicKey,
                "198.51.100.1:40000", registration.LocalEndpoints)
        ]);
        await using var snapshotStream = new MemoryStream();
        await MeshControlProtocol.WriteSnapshotAsync(snapshotStream, snapshot);
        snapshotStream.Position = 0;
        MeshSnapshot decoded = await MeshControlProtocol.ReadSnapshotAsync(snapshotStream);
        MeshPeerDescriptor peer = Assert.Single(decoded.Peers);
        Assert.Equal("alice", peer.NodeName);
        Assert.Equal("10.8.0.2", peer.Ipv4Address);
        Assert.Equal("fd42:8::2", peer.Ipv6Address);
        Assert.Equal("198.51.100.1:40000", peer.ReflexiveEndpoint);
    }

    [Fact]
    public async Task RoundTripsSessionToken()
    {
        const string token = "0123456789abcdef0123456789abcdef";
        await using var stream = new MemoryStream();
        await MeshSessionProtocol.WriteAsync(stream, token);
        stream.Position = 0;
        Assert.Equal(token, await MeshSessionProtocol.ReadAsync(stream));
    }
}
