using System.Net;
using System.Threading.Channels;
using VpnSample.Dns;
using VpnSample.Protocol;

namespace VpnSample.Mesh;

public sealed class MeshCoordinator
{
    readonly object sessionsLock = new();
    readonly Dictionary<string, SessionState> sessions = new(StringComparer.Ordinal);

    public string RegisterDataSession(string nodeName, TunnelAddresses addresses)
    {
        string normalizedName = DnsName.NormalizeNodeName(nodeName);
        string token;
        lock (sessionsLock)
        {
            do
            {
                token = System.Security.Cryptography.RandomNumberGenerator.GetHexString(
                    MeshSessionProtocol.TokenLength,
                    lowercase: true);
            }
            while (sessions.ContainsKey(token));

            sessions.Add(token, new SessionState(normalizedName, addresses));
        }
        return token;
    }

    public async Task RunControlSessionAsync(
        string sessionToken,
        Stream transport,
        CancellationToken cancellationToken)
    {
        MeshSessionProtocol.Validate(sessionToken);
        MeshRegistrationRequest registration =
            await MeshControlProtocol.ReadRegistrationAsync(transport, cancellationToken);
        string normalizedName = DnsName.NormalizeNodeName(registration.NodeName);
        ValidatePublicKey(registration.PublicKey);
        string[] localEndpoints = registration.LocalEndpoints
            .Select(ParseEndpoint)
            .Distinct()
            .Select(FormatEndpoint)
            .ToArray();

        SessionState session;
        lock (sessionsLock)
        {
            if (!sessions.TryGetValue(sessionToken, out session!) ||
                !StringComparer.Ordinal.Equals(session.NodeName, normalizedName))
            {
                throw new InvalidOperationException("The mesh session is not active for this node.");
            }
            if (session.ControlAttached)
                throw new InvalidOperationException("The mesh session already has a control connection.");

            session.PublicKey = registration.PublicKey;
            session.LocalEndpoints = localEndpoints;
            session.ControlAttached = true;
            BroadcastSnapshotsLocked();
        }

        try
        {
            await foreach (MeshSnapshot snapshot in
                session.Updates.Reader.ReadAllAsync(cancellationToken))
            {
                await MeshControlProtocol.WriteSnapshotAsync(
                    transport,
                    snapshot,
                    cancellationToken);
            }
        }
        finally
        {
            lock (sessionsLock)
            {
                if (sessions.GetValueOrDefault(sessionToken) == session)
                {
                    session.ControlAttached = false;
                    session.PublicKey = null;
                    session.LocalEndpoints = [];
                    BroadcastSnapshotsLocked();
                }
            }
        }
    }

    public bool TryUpdateReflexiveEndpoint(string sessionToken, IPEndPoint endpoint)
    {
        string formattedEndpoint = FormatEndpoint(endpoint);
        lock (sessionsLock)
        {
            if (!sessions.TryGetValue(sessionToken, out SessionState? session))
                return false;
            if (!StringComparer.Ordinal.Equals(session.ReflexiveEndpoint, formattedEndpoint))
            {
                session.ReflexiveEndpoint = formattedEndpoint;
                BroadcastSnapshotsLocked();
            }
            return true;
        }
    }

    public void UnregisterDataSession(string sessionToken)
    {
        lock (sessionsLock)
        {
            if (!sessions.Remove(sessionToken, out SessionState? removed))
                return;
            removed.Updates.Writer.TryComplete();
            BroadcastSnapshotsLocked();
        }
    }

    public bool ContainsSession(string sessionToken)
    {
        lock (sessionsLock)
            return sessions.ContainsKey(sessionToken);
    }

    void BroadcastSnapshotsLocked()
    {
        MeshPeerDescriptor[] peers = sessions.Values
            .Where(session => session.ControlAttached && session.PublicKey is not null)
            .Select(session => new MeshPeerDescriptor(
                session.NodeName,
                session.Addresses.ClientIpv4,
                session.Addresses.ClientIpv6,
                session.PublicKey!,
                session.ReflexiveEndpoint,
                session.LocalEndpoints))
            .OrderBy(peer => peer.NodeName, StringComparer.Ordinal)
            .ToArray();
        var snapshot = new MeshSnapshot(peers);
        foreach (SessionState session in sessions.Values.Where(value => value.ControlAttached))
            session.Updates.Writer.TryWrite(snapshot);
    }

    static void ValidatePublicKey(string publicKey)
    {
        try
        {
            byte[] encoded = Convert.FromBase64String(publicKey);
            if (encoded.Length is < 64 or > 256)
                throw new FormatException();
        }
        catch (FormatException error)
        {
            throw new InvalidDataException("The mesh public key is invalid.", error);
        }
    }

    static IPEndPoint ParseEndpoint(string value) =>
        IPEndPoint.TryParse(value, out IPEndPoint? endpoint) &&
        endpoint.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork &&
        endpoint.Port != 0
            ? endpoint
            : throw new InvalidDataException($"Invalid IPv4 mesh candidate '{value}'.");

    static string FormatEndpoint(IPEndPoint endpoint) => endpoint.ToString();

    sealed class SessionState(string nodeName, TunnelAddresses addresses)
    {
        public string NodeName { get; } = nodeName;
        public TunnelAddresses Addresses { get; } = addresses;
        public Channel<MeshSnapshot> Updates { get; } =
            Channel.CreateBounded<MeshSnapshot>(new BoundedChannelOptions(8)
            {
                SingleReader = true,
                SingleWriter = false,
                FullMode = BoundedChannelFullMode.DropOldest
            });
        public string? PublicKey { get; set; }
        public string? ReflexiveEndpoint { get; set; }
        public IReadOnlyList<string> LocalEndpoints { get; set; } = [];
        public bool ControlAttached { get; set; }
    }
}
