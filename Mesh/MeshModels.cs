namespace VpnSample.Mesh;

public sealed record MeshRegistrationRequest(
    string NodeName,
    string PublicKey,
    IReadOnlyList<string> LocalEndpoints);

public sealed record MeshPeerDescriptor(
    string NodeName,
    string Ipv4Address,
    string Ipv6Address,
    string PublicKey,
    string? ReflexiveEndpoint,
    IReadOnlyList<string> LocalEndpoints);

public sealed record MeshSnapshot(IReadOnlyList<MeshPeerDescriptor> Peers);
