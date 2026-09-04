using System.Buffers;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading.Channels;
using VpnSample.Dns;
using VpnSample.Protocol;

namespace VpnSample.Mesh;

public sealed class MeshPacketEndpoint : IPacketEndpoint, IAsyncDisposable
{
    static readonly TimeSpan DirectPathLifetime = TimeSpan.FromSeconds(20);
    readonly IPacketEndpoint tun;
    readonly string nodeName;
    readonly IPAddress ownIpv4;
    readonly IPAddress ownIpv6;
    readonly MeshIdentity identity;
    readonly UdpClient udp = new(new IPEndPoint(IPAddress.Any, 0));
    readonly SemaphoreSlim udpSendLock = new(1, 1);
    readonly SemaphoreSlim tunWriteLock = new(1, 1);
    readonly PacketChannelReadStream relayReader = new();
    readonly RelayPacketWriteStream relayWriter;
    readonly object peersLock = new();
    readonly Dictionary<string, PeerState> peersByName = new(StringComparer.Ordinal);
    readonly Dictionary<IPAddress, PeerState> routes = [];
    readonly CancellationTokenSource stop = new();
    readonly Action<string> log;
    IPEndPoint? rendezvousEndpoint;
    string? sessionToken;
    Task? completion;
    long nextSequence;
    int isStarted;
    int isDisposed;

    public MeshPacketEndpoint(
        IPacketEndpoint tun,
        string nodeName,
        string ownIpv4,
        string ownIpv6,
        Action<string>? log = null,
        string? identityPath = null,
        Action<Socket>? configureUdpSocket = null)
    {
        ArgumentNullException.ThrowIfNull(tun);
        this.tun = tun;
        this.nodeName = DnsName.NormalizeNodeName(nodeName);
        this.ownIpv4 = IPAddress.Parse(ownIpv4);
        this.ownIpv6 = IPAddress.Parse(ownIpv6);
        this.log = log ?? Console.WriteLine;
        identity = new MeshIdentity(identityPath);
        configureUdpSocket?.Invoke(udp.Client);
        relayWriter = new RelayPacketWriteStream(this);
    }

    public Stream PacketReader => relayReader;
    public Stream PacketWriter => relayWriter;
    public string PublicKey => identity.PublicKey;
    public int UdpPort => ((IPEndPoint)udp.Client.LocalEndPoint!).Port;
    public Task Completion => completion ?? Task.CompletedTask;

    public IReadOnlyList<string> GetLocalCandidates() =>
        NetworkInterface.GetAllNetworkInterfaces()
            .Where(networkInterface => networkInterface.OperationalStatus == OperationalStatus.Up)
            .SelectMany(networkInterface => networkInterface.GetIPProperties().UnicastAddresses)
            .Select(address => address.Address)
            .Where(address => IsUnderlayCandidate(address, ownIpv4))
            .Distinct()
            .Select(address => new IPEndPoint(address, UdpPort).ToString())
            .Order(StringComparer.Ordinal)
            .ToArray();

    internal static bool IsUnderlayCandidate(IPAddress address, IPAddress overlayAddress) =>
        address.AddressFamily == AddressFamily.InterNetwork &&
        !IPAddress.IsLoopback(address) &&
        !address.Equals(IPAddress.Any) &&
        !address.Equals(overlayAddress);

    public async Task StartAsync(
        IPEndPoint rendezvousEndpoint,
        string sessionToken,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(isDisposed != 0, this);
        if (Interlocked.Exchange(ref isStarted, 1) != 0)
            throw new InvalidOperationException("The mesh endpoint is already running.");
        MeshSessionProtocol.Validate(sessionToken);
        this.rendezvousEndpoint = rendezvousEndpoint;
        this.sessionToken = sessionToken;
        await SendRendezvousRegistrationAsync(cancellationToken);
        completion = Task.WhenAll(
            RouteTunPacketsAsync(stop.Token),
            ReceiveDatagramsAsync(stop.Token),
            MaintainPathsAsync(stop.Token));
    }

    public void UpdatePeers(MeshSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        lock (peersLock)
        {
            var retainedNames = new HashSet<string>(StringComparer.Ordinal);
            foreach (MeshPeerDescriptor descriptor in snapshot.Peers)
            {
                string peerName = DnsName.NormalizeNodeName(descriptor.NodeName);
                if (StringComparer.Ordinal.Equals(peerName, nodeName))
                    continue;

                retainedNames.Add(peerName);
                if (!peersByName.TryGetValue(peerName, out PeerState? peer) ||
                    !StringComparer.Ordinal.Equals(peer.PublicKey, descriptor.PublicKey))
                {
                    peer = new PeerState(peerName, descriptor.PublicKey,
                        identity.DerivePeerKey(descriptor.PublicKey));
                    peersByName[peerName] = peer;
                }
                peer.Update(descriptor);
            }

            foreach (string removed in peersByName.Keys.Where(name => !retainedNames.Contains(name)).ToArray())
                peersByName.Remove(removed);
            routes.Clear();
            foreach (PeerState peer in peersByName.Values)
            {
                routes[peer.Ipv4Address] = peer;
                routes[peer.Ipv6Address] = peer;
            }
        }
    }

    public ValueTask InterruptReadAsync()
    {
        relayReader.Complete();
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref isDisposed, 1) != 0)
            return;
        stop.Cancel();
        relayReader.Complete();
        await tun.InterruptReadAsync();
        if (completion is not null)
        {
            try
            {
                await completion;
            }
            catch (OperationCanceledException)
            {
            }
            catch (ObjectDisposedException)
            {
            }
        }
        udp.Dispose();
        identity.Dispose();
        udpSendLock.Dispose();
        tunWriteLock.Dispose();
        stop.Dispose();
    }

    async Task RouteTunPacketsAsync(CancellationToken cancellationToken)
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(TunnelFrame.MaximumPayloadLength);
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int length = await tun.PacketReader.ReadAsync(buffer, cancellationToken);
                if (length == 0)
                    throw new EndOfStreamException("The TUN packet endpoint closed.");
                byte[] packet = buffer.AsSpan(0, length).ToArray();
                PacketAddresses addresses = PacketAddresses.Parse(packet);
                PeerState? peer = FindPeer(addresses.Destination);
                if (peer is null || !await TrySendDirectAsync(peer, packet, cancellationToken))
                    await relayReader.EnqueueAsync(packet, cancellationToken);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
            relayReader.Complete();
        }
    }

    async Task ReceiveDatagramsAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            UdpReceiveResult received = await udp.ReceiveAsync(cancellationToken);
            if (UdpRendezvousProtocol.IsAcknowledgement(received.Buffer))
                continue;
            if (!SecureMeshDatagram.TryReadSender(
                    received.Buffer,
                    out string senderName,
                    out _) ||
                !TryGetPeer(senderName, out PeerState peer) ||
                !SecureMeshDatagram.TryDecrypt(
                    received.Buffer,
                    senderName,
                    peer.Key,
                    out MeshDatagramType type,
                    out ulong noncePrefix,
                    out uint sequence,
                    out byte[] plaintext) ||
                !peer.Replay.TryAccept(noncePrefix, sequence))
            {
                continue;
            }

            peer.MarkAuthenticatedPath(received.RemoteEndPoint, log);
            switch (type)
            {
                case MeshDatagramType.Probe:
                    await SendEncryptedAsync(peer, received.RemoteEndPoint,
                        MeshDatagramType.ProbeAcknowledgement,
                        ReadOnlyMemory<byte>.Empty,
                        cancellationToken);
                    break;
                case MeshDatagramType.Data when IsValidPeerPacket(peer, plaintext):
                    peer.LogFirstData(log, "received");
                    await WriteTunAsync(plaintext, cancellationToken);
                    break;
                case MeshDatagramType.ProbeAcknowledgement:
                case MeshDatagramType.Keepalive:
                case MeshDatagramType.Data:
                    break;
            }
        }
    }

    async Task MaintainPathsAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(3));
        while (true)
        {
            await SendRendezvousRegistrationAsync(cancellationToken);
            PeerState[] peers;
            lock (peersLock)
                peers = peersByName.Values.ToArray();
            foreach (PeerState peer in peers)
            {
                foreach (IPEndPoint endpoint in peer.GetProbeEndpoints())
                    await SendEncryptedAsync(
                        peer,
                        endpoint,
                        MeshDatagramType.Probe,
                        ReadOnlyMemory<byte>.Empty,
                        cancellationToken);
            }
            await timer.WaitForNextTickAsync(cancellationToken);
        }
    }

    async Task<bool> TrySendDirectAsync(
        PeerState peer,
        byte[] packet,
        CancellationToken cancellationToken)
    {
        IPEndPoint? endpoint = peer.GetActiveEndpoint(DirectPathLifetime);
        if (endpoint is null || packet.Length > 60_000)
            return false;
        try
        {
            await SendEncryptedAsync(peer, endpoint, MeshDatagramType.Data, packet, cancellationToken);
            peer.LogFirstData(log, "sent");
            return true;
        }
        catch (SocketException error)
        {
            log($"Direct mesh send to {peer.NodeName} failed: {error.Message}; using relay.");
            peer.ClearPath();
            return false;
        }
    }

    async Task SendRendezvousRegistrationAsync(CancellationToken cancellationToken)
    {
        if (rendezvousEndpoint is null || sessionToken is null)
            return;
        await SendUdpAsync(
            UdpRendezvousProtocol.CreateRegistration(sessionToken),
            rendezvousEndpoint,
            cancellationToken);
    }

    async Task SendEncryptedAsync(
        PeerState peer,
        IPEndPoint endpoint,
        MeshDatagramType type,
        ReadOnlyMemory<byte> plaintext,
        CancellationToken cancellationToken)
    {
        uint sequence = checked((uint)Interlocked.Increment(ref nextSequence));
        byte[] datagram = SecureMeshDatagram.Encrypt(
            type,
            nodeName,
            peer.Key,
            identity.NoncePrefix,
            sequence,
            plaintext.Span);
        await SendUdpAsync(datagram, endpoint, cancellationToken);
    }

    async Task SendUdpAsync(
        byte[] datagram,
        IPEndPoint endpoint,
        CancellationToken cancellationToken)
    {
        await udpSendLock.WaitAsync(cancellationToken);
        try
        {
            await udp.SendAsync(datagram, endpoint, cancellationToken);
        }
        finally
        {
            udpSendLock.Release();
        }
    }

    async Task WriteTunAsync(byte[] packet, CancellationToken cancellationToken)
    {
        await tunWriteLock.WaitAsync(cancellationToken);
        try
        {
            await tun.PacketWriter.WriteAsync(packet, cancellationToken);
        }
        finally
        {
            tunWriteLock.Release();
        }
    }

    bool IsValidPeerPacket(PeerState peer, byte[] packet)
    {
        try
        {
            PacketAddresses addresses = PacketAddresses.Parse(packet);
            return peer.Owns(addresses.Source) &&
                (addresses.Destination.Equals(ownIpv4) || addresses.Destination.Equals(ownIpv6));
        }
        catch (InvalidDataException)
        {
            return false;
        }
    }

    PeerState? FindPeer(IPAddress address)
    {
        lock (peersLock)
            return routes.GetValueOrDefault(address);
    }

    bool TryGetPeer(string name, out PeerState peer)
    {
        lock (peersLock)
        {
            if (peersByName.TryGetValue(name, out PeerState? found))
            {
                peer = found;
                return true;
            }
        }
        peer = null!;
        return false;
    }

    sealed class RelayPacketWriteStream(MeshPacketEndpoint endpoint) : Stream
    {
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }
        public override ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default) =>
            new(endpoint.WriteTunAsync(buffer.ToArray(), cancellationToken));
        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    sealed class PacketChannelReadStream : Stream
    {
        readonly Channel<byte[]> packets = Channel.CreateBounded<byte[]>(
            new BoundedChannelOptions(256)
            {
                SingleReader = true,
                SingleWriter = true,
                FullMode = BoundedChannelFullMode.Wait
            });

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public ValueTask EnqueueAsync(byte[] packet, CancellationToken cancellationToken) =>
            packets.Writer.WriteAsync(packet, cancellationToken);
        public void Complete() => packets.Writer.TryComplete();

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            try
            {
                byte[] packet = await packets.Reader.ReadAsync(cancellationToken);
                if (packet.Length > buffer.Length)
                    throw new InvalidDataException("A mesh packet exceeds the read buffer.");
                packet.CopyTo(buffer);
                return packet.Length;
            }
            catch (ChannelClosedException)
            {
                return 0;
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                Complete();
            base.Dispose(disposing);
        }
        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    sealed class PeerState
    {
        readonly object pathLock = new();
        IPEndPoint[] candidates = [];
        IPEndPoint? activeEndpoint;
        DateTimeOffset lastAuthenticated;
        bool sentDataLogged;
        bool receivedDataLogged;

        public PeerState(string nodeName, string publicKey, byte[] key)
        {
            NodeName = nodeName;
            PublicKey = publicKey;
            Key = key;
        }

        public string NodeName { get; }
        public string PublicKey { get; }
        public byte[] Key { get; }
        public ReplayWindow Replay { get; } = new();
        public IPAddress Ipv4Address { get; private set; } = IPAddress.None;
        public IPAddress Ipv6Address { get; private set; } = IPAddress.IPv6None;

        public void Update(MeshPeerDescriptor descriptor)
        {
            Ipv4Address = IPAddress.Parse(descriptor.Ipv4Address);
            Ipv6Address = IPAddress.Parse(descriptor.Ipv6Address);
            candidates = descriptor.LocalEndpoints
                .Append(descriptor.ReflexiveEndpoint)
                .Where(value => value is not null)
                .Select(value => IPEndPoint.Parse(value!))
                .Distinct()
                .ToArray();
        }

        public bool Owns(IPAddress address) =>
            address.Equals(Ipv4Address) || address.Equals(Ipv6Address);

        public IPEndPoint[] GetProbeEndpoints()
        {
            lock (pathLock)
                return activeEndpoint is null
                    ? candidates.ToArray()
                    : candidates.Append(activeEndpoint).Distinct().ToArray();
        }

        public IPEndPoint? GetActiveEndpoint(TimeSpan lifetime)
        {
            lock (pathLock)
                return DateTimeOffset.UtcNow - lastAuthenticated <= lifetime
                    ? activeEndpoint
                    : null;
        }

        public void MarkAuthenticatedPath(IPEndPoint endpoint, Action<string> log)
        {
            lock (pathLock)
            {
                bool currentPathIsFresh = activeEndpoint is not null &&
                    DateTimeOffset.UtcNow - lastAuthenticated <= DirectPathLifetime;
                if (currentPathIsFresh && !Equals(activeEndpoint, endpoint))
                    return;
                if (!Equals(activeEndpoint, endpoint))
                    log($"Direct mesh path: {NodeName}.vpn via udp://{endpoint}");
                activeEndpoint = endpoint;
                lastAuthenticated = DateTimeOffset.UtcNow;
            }
        }

        public void ClearPath()
        {
            lock (pathLock)
                activeEndpoint = null;
        }

        public void LogFirstData(Action<string> log, string direction)
        {
            lock (pathLock)
            {
                if (direction == "sent")
                {
                    if (sentDataLogged)
                        return;
                    sentDataLogged = true;
                }
                else
                {
                    if (receivedDataLogged)
                        return;
                    receivedDataLogged = true;
                }
                log($"Direct mesh data {direction}: {NodeName}.vpn");
            }
        }
    }
}
