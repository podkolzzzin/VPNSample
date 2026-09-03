# Serverless Peer-to-Peer VPN Design

The project will need to evolve from a client/server TCP tunnel into a
**peer-to-peer encrypted UDP mesh**.

In this context, "serverless" should mean that application traffic travels
directly between peers. It does not necessarily mean that discovery,
coordination, STUN, and fallback relay servers do not exist.

## Target architecture

```text
                         Coordination service
                    keys, peers, addresses, policy
                              /       \
                             /         \
                            v           v
TUN <-> Overlay router <-> Secure UDP <-> NAT <----> NAT <-> Secure UDP <-> Overlay router <-> TUN
                            \                       /
                             +---- relay fallback -+
```

Tailscale similarly separates its centralized control plane from its
peer-to-peer data plane. Its coordination service distributes keys, addresses,
and policy but normally does not carry VPN traffic. See
[How Tailscale works](https://tailscale.com/blog/how-tailscale-works).

## 1. UDP transport

Replace the current TCP `NetworkStream` data plane with a packet-preserving
abstraction:

```csharp
public interface IDatagramTransport
{
    ValueTask SendAsync(
        ReadOnlyMemory<byte> datagram,
        IPEndPoint destination,
        CancellationToken cancellationToken);

    ValueTask<ReceivedDatagram> ReceiveAsync(
        CancellationToken cancellationToken);
}
```

Hole punching depends on both peers sending UDP packets outward. TCP is much
less suitable because establishing simultaneous TCP connections through
arbitrary NATs is harder and less portable.

Use one stable UDP socket for:

- STUN requests.
- Connectivity probes.
- Encrypted VPN traffic.
- NAT keepalives.

Using the same socket is essential because the discovered NAT mapping belongs
to that specific local UDP endpoint.

## 2. Encryption and peer identity

The current tunnel is explicitly unencrypted and unauthenticated. Before
exposing peer UDP sockets, add:

- A persistent node key pair.
- Authenticated peer public keys.
- An authenticated key exchange.
- Replay protection.
- AEAD encryption.
- Key rotation and revocation.

For anything beyond a protocol-learning experiment, use WireGuard instead of
designing new cryptography. WireGuard already provides authenticated
encryption, replay protection, roaming between endpoints, and keepalives. Its
endpoint roaming is particularly valuable when a peer's NAT mapping changes.
See the [WireGuard protocol paper](https://www.wireguard.com/papers/wireguard.pdf).

On Linux, the control process could configure kernel WireGuard through its
documented Netlink interface while retaining responsibility for discovery and
coordination. See the
[Linux WireGuard Netlink specification](https://docs.kernel.org/next/netlink/specs/wireguard.html).

## 3. Candidate gathering

Each peer must collect possible addresses through which it can be reached:

- Local IPv4 addresses.
- Local IPv6 addresses.
- Public server-reflexive addresses discovered through STUN.
- Port mappings created with PCP, NAT-PMP, or UPnP, optionally.
- Relay addresses, when available.

STUN tells a peer which public IP address and port its NAT assigned, but STUN
alone is not a complete traversal solution. See
[RFC 8489](https://www.rfc-editor.org/info/rfc8489/).

```csharp
public interface ICandidateGatherer
{
    IAsyncEnumerable<EndpointCandidate> GatherAsync(
        Socket sharedSocket,
        CancellationToken cancellationToken);
}
```

## 4. Rendezvous and coordination

The two peers must exchange:

- Node identity and public key.
- Overlay IP address.
- Endpoint candidates.
- Session credentials for connectivity probes.
- Supported protocol versions and features.
- Network access policy.

The practical solution is a small HTTPS/WebSocket coordination server. It
handles no unencrypted VPN traffic and consumes little bandwidth.

For a coordination-server-free demonstration, users could exchange a signed
invitation blob manually:

```text
public key + overlay address + candidates + expiration + signature
```

This has important limitations: candidates change, NAT mappings expire, and
two offline peers cannot rendezvous. Public STUN services are also servers. A
DHT changes where coordination happens but does not eliminate bootstrapping or
NAT traversal.

## 5. Connectivity checks and hole punching

Implement a simplified ICE-like state machine:

1. Both peers bind their UDP sockets.
2. They gather local and STUN candidates.
3. They exchange candidates through coordination.
4. Both begin sending authenticated probes to every reasonable candidate pair.
5. Successful bidirectional paths are measured.
6. The best path is selected.
7. Probing continues periodically so that a better or replacement path can be
   selected.

ICE formalizes candidate pairing, connectivity checks, roles, nomination, and
failover. See [RFC 8445](https://www.rfc-editor.org/info/rfc8445/).

Suggested abstractions:

```csharp
public interface IRendezvousClient { /* publish and watch peers */ }
public interface IConnectivityChecker { /* probe candidate pairs */ }
public interface IPathSelector { /* select and migrate paths */ }
public interface IPeerDirectory { /* keys, overlay IPs, policy */ }
```

## 6. Keepalives and path migration

NAT mappings expire after idle periods. Send authenticated keepalives only
when necessary and immediately repeat discovery when:

- Wi-Fi or Ethernet changes.
- The host resumes from sleep.
- Its external address changes.
- Connectivity probes stop receiving replies.
- A lower-latency path appears.

WireGuard recommends persistent keepalives for peers that must remain
reachable behind NAT and gives 25 seconds as a broadly useful interval. See
the [WireGuard quick start](https://www.wireguard.com/quickstart/).

## 7. Relay fallback

Direct connections will sometimes be impossible because of:

- Endpoint-dependent or symmetric NAT.
- Carrier-grade NAT.
- Firewalls blocking UDP.
- Networks allowing only web traffic.

A reliable system therefore needs a TURN- or DERP-like relay carrying packets
that are already encrypted end to end. TURN is the standardized option. See
[RFC 8656](https://www.rfc-editor.org/info/rfc8656/).

A useful connection strategy is:

```text
connect through relay immediately
        +
probe direct paths concurrently
        |
        v
switch to UDP P2P when one succeeds
        |
        v
return to relay if the direct path fails
```

Tailscale uses this relay-first, direct-upgrade model. See
[How NAT traversal works](https://tailscale.com/blog/how-nat-traversal-works).

## 8. Overlay routing

A mesh node replaces the current single remote server with a peer table:

```csharp
public sealed record PeerRoute(
    IPNetwork AllowedNetwork,
    PeerId Peer,
    PublicKey PublicKey);
```

For each TUN packet:

1. Parse its destination.
2. Find the longest matching overlay route.
3. Apply the selected demonstration pipeline.
4. Encrypt it for the selected peer.
5. Send it through the peer's selected direct or relay path.

Split tunneling naturally becomes a combination of operating-system routes and
peer `AllowedNetworks`.

## Recommended internal pipeline

```text
TUN packet
  -> overlay route lookup
  -> demonstration stages
  -> WireGuard/security layer
  -> path selector
  -> direct UDP or encrypted relay
```

Encryption should occur after modifications to the authenticated inner frame.
Otherwise splitting, masking, or reordering code could invalidate
authentication.

HTTP masking should normally be a property of a relay path rather than the
primary hole-punched path:

```text
Direct path: UDP datagrams
Fallback:    encrypted frames over HTTPS/WebSocket
```

## Suggested implementation order

1. Direct UDP tunnel between two public addresses.
2. Encryption and authenticated peer identity.
3. Multiple peers and overlay route lookup.
4. STUN using the same UDP socket as tunnel traffic.
5. Minimal coordination service.
6. Simultaneous authenticated connectivity probes.
7. Keepalives and automatic path migration.
8. TURN- or DERP-like relay fallback.
9. ACLs, key rotation, device enrollment, and revocation.
10. Optional DNS names, subnet routers, and exit nodes.

The smallest credible first milestone is **two encrypted peers, a manual
invitation blob, one STUN server, and direct UDP punching**. The smallest
reliable Tailscale-like system additionally needs coordination and relay
services.
