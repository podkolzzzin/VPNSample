# Simple IP tunnel (.NET 10 / C#)

A small Linux-only learning project. Every client gets a dual-stack TUN interface
in one shared overlay subnet. Client-to-client traffic normally travels directly
over an end-to-end encrypted UDP path; the server coordinates peers and remains a
WSS relay fallback, private DNS server, and IPv4/IPv6 exit node.

Clients register a node name when they connect. The separate `VpnSample.Dns`
assembly serves authoritative A and AAAA records in the private `.vpn` zone, so
nodes can reach one another as, for example, `nginx-node.vpn`.

Current architecture, deployment flow, and IPv4/IPv6 routing diagrams are in
[ARCHITECTURE.md](ARCHITECTURE.md).

The control plane and relay use standard RFC 6455 WebSockets at
`wss://vpn.twocubes.io/api/v1/mesh` and `/api/v1/events`. Peers use the same
stable UDP socket for rendezvous, authenticated probes, NAT keepalives, and
encrypted packets. The HTTPS root still serves an ordinary cover page and
unauthenticated probes receive `404`.

Each node persists a P-256 identity. Coordination distributes public keys and
local/server-reflexive endpoint candidates; peers derive pairwise keys and use
AES-256-GCM plus a replay window on the direct path. This is intentionally a
learning protocol, not a replacement for WireGuard or a production VPN.

The default `websocket-cover` profile reorders windows of up to three IP packets,
splits each into fragments of at most 240 bytes, and pads frames into size buckets.
The earlier `shuffle-split` and no-tricks `baseline` profiles remain available.

## Requirements

- Linux on both machines
- .NET 10 SDK
- `iproute2`; `systemd-resolved`; `iptables` for internet forwarding; `openssl`
  for demo certificates
- root privileges for `/dev/net/tun` and network configuration
- TCP and UDP port 443 allowed through the server firewall

## Build

From this directory:

```bash
dotnet build VPNSample.slnx
```

This commit is the eighth demo stage, `stage-08-peer-to-peer-mesh`.

Move through the tagged demo stages from any checked-out stage:

```bash
./scripts/checkout_next_tag.sh
./scripts/checkout_prev_tag.sh
```

Both scripts fetch tags, refuse to replace uncommitted work, and switch in
detached HEAD mode. Start explicitly with `git switch --detach
stage-01-basic-tunnel` when HEAD is not already on a demo stage.

## Automated DigitalOcean flow

The scripts require `doctl`, `ssh`, `scp`, `ssh-keygen`, and the .NET 10 SDK
locally. Authenticate `doctl` first.

The script layout and local validation commands are documented in
[scripts/README.md](scripts/README.md).

```bash
./scripts/create-droplet.sh
./scripts/deploy-server.sh
./scripts/run-vpn.sh
```

`create-droplet.sh` generates a dedicated passwordless Ed25519 key, registers its
public key in DigitalOcean, and records the temporary resources in
`.vpn-droplet.env`. Use `--ssh-key-id` together with `--ssh-key` only when you
explicitly want to supply an existing key. `deploy-server.sh` publishes the
server, installs .NET 10 on the IPv6-enabled droplet, configures IPv4/IPv6
forwarding and NAT, creates a seven-day certificate and random WebSocket token,
and starts `vpnsample.service`. The public certificate and token are recorded in
the state file; the private key exists only on the server. Deployment verifies
that `/` returns the cover page and an unauthenticated tunnel probe returns `404`.

The temporary flow connects to the droplet IP while using `vpn.twocubes.io` as
the WebSocket URI, TLS SNI, and HTTP `Host`, so it does not require a DNS change.
For a permanent
deployment, point the `vpn.twocubes.io` A/AAAA records at the server, issue a
publicly trusted certificate, and provide its PEM files to `deploy-server.sh`:

```bash
VPN_TLS_CERTIFICATE=/path/to/fullchain.pem \
VPN_TLS_PRIVATE_KEY=/path/to/privkey.pem \
./scripts/deploy-server.sh
```

The server itself returns a small cover site at `/`. To preserve a different
existing site, put this service behind its reverse proxy and forward
`/api/v1/events` plus `/api/v1/mesh`; UDP/443 must still reach the rendezvous
socket directly. A network observer can still see the destination IP, TLS SNI,
UDP endpoints, traffic volume, and timing.

By default, `run-vpn.sh` replaces the local IPv4 and IPv6 default routes while the
client is running and restores them on exit. Use `--peer-only` to create and test
the tunnel without changing either default route. The VPN IPv6 route uses metric
50 so it wins over typical router-advertisement routes; override this with
`VPN_ROUTE_METRIC` if the host has a still-lower-priority metric. In full-tunnel
mode the script marks the mesh UDP socket and installs a temporary IPv4 policy
table for the VPN default. Marked rendezvous and direct-peer datagrams bypass
that table and retain the original WAN or LAN route. This prevents the mesh
transport from recursively entering its own TUN. The
mark, table, and rule priority can be changed with `VPN_MESH_SOCKET_MARK`,
`VPN_MESH_ROUTE_TABLE`, and `VPN_MESH_RULE_PRIORITY`.

Always remove the temporary droplet when finished:

```bash
./scripts/create-droplet.sh --delete
```

This also removes the generated key from DigitalOcean and deletes the local
`.vpn-ssh-key` and `.vpn-ssh-key.pub` files.

### Three-node mesh E2E

To reproduce the server plus two-client test in three DigitalOcean regions:

```bash
./scripts/e2e-three-node.sh
```

The script creates a VPN server in Amsterdam, an nginx client in Frankfurt,
and a requester client in New York. It verifies bidirectional IPv4 and IPv6
ping, waits for authenticated direct UDP paths on both clients, proves peer
packets were sent and received through those paths, checks internet
reachability through the exit node, resolves both private node names, fetches
nginx as `nginx-node.vpn` over both tunnel address families, checks nginx's
access log for the requester's overlay addresses, and deletes all temporary
resources on exit. The node names can be overridden with `CLIENT_A_NAME` and
`CLIENT_B_NAME`. The regions and droplet
size can be overridden with `SERVER_REGION`, `CLIENT_A_REGION`,
`CLIENT_B_REGION`, and `DO_SIZE`.

## Run the server

First enable IPv4 and IPv6 forwarding. Replace `eth0` with the server's internet
interface:

```bash
sudo sysctl -w net.ipv4.ip_forward=1
sudo sysctl -w net.ipv6.conf.all.forwarding=1
sudo iptables -t nat -A POSTROUTING -s 10.8.0.0/24 -o eth0 -j MASQUERADE
sudo ip6tables -t nat -A POSTROUTING -s fd42:8::/64 -o eth0 -j MASQUERADE
sudo env \
  VPN_TLS_SERVER_NAME=vpn.twocubes.io \
  VPN_TLS_CERTIFICATE=/path/to/fullchain.pem \
  VPN_TLS_PRIVATE_KEY=/path/to/privkey.pem \
  VPN_COVER_TOKEN=0123456789abcdef0123456789abcdef \
  dotnet run --project Server -- 443
```

## Run the client

Connect using the server address:

```bash
sudo env \
  VPN_TLS_SERVER_NAME=vpn.twocubes.io \
  VPN_TLS_PINNED_CERTIFICATE=/path/to/server.crt \
  VPN_COVER_TOKEN=0123456789abcdef0123456789abcdef \
  VPN_MESH_KEY_FILE=/path/to/persistent-mesh.key \
  dotnet run --project Client -- SERVER_IP 443 my-laptop
```

The optional final argument is the node's `.vpn` name. Without it, the client
uses `Environment.MachineName`. The name must be one DNS label of 1-63 letters,
digits, or hyphens; names are case-insensitive and must be unique among connected
clients. `scripts/run-vpn.sh --name my-laptop` also configures `systemd-resolved`
to use `10.8.0.1` for the `.vpn` zone and restores the previous per-link DNS
settings when the VPN stops.
The wrapper stores the persistent node identity in `.vpn-mesh-key` by default;
set `VPN_MESH_KEY_FILE` to choose another location.

Once two clients are connected, applications can use their private names:

```bash
ping nginx-node.vpn
curl http://nginx-node.vpn/
```

The server prints the assigned addresses. Client zero can test its peer with:

```bash
ping 10.8.0.1
ping -6 fd42:8::1
```

## Packet tracing

Set `VPN_TRACE_PACKETS=1` on both processes to print one compact line for every
raw IP packet at the TUN/tunnel boundary. Each line contains a short SHA-256
fingerprint, protocol, endpoints, and length:

```text
19:42:10.381Z [client]          SEND #8F21A4C7735E98D2  TCP    10.8.0.2:42100 → 1.1.1.1:443  60 B
19:42:10.394Z [server client=0] RECV #8F21A4C7735E98D2  TCP    10.8.0.2:42100 → 1.1.1.1:443  60 B
```

Matching fingerprints identify the same packet on both sides. The display uses
the first 64 SHA-256 bits; use PCAP or the hex dump when byte-for-byte proof
matters.

Set `VPN_TRACE_HEX=1` to add a conventional 16-byte-wide dump. This option also
enables the compact summary, even when `VPN_TRACE_PACKETS` is unset:

```text
  0000  45 00 00 3C 12 34 40 00  40 06 00 00 0A 08 00 02  | E..<.4@.@.......
  0010  01 01 01 01 A4 74 01 BB  12 34 56 78 00 00 00 00  | .....t...4Vx....
```

Set `VPN_TRACE_PCAP` to write the complete raw packets to classic PCAP files
that Wireshark and `tcpdump` can open. The side is appended automatically, or
can be positioned explicitly with `{side}`:

```bash
VPN_TRACE_PCAP=/tmp/vpn.pcap ./scripts/run-vpn.sh
# Writes /tmp/vpn-client.pcap

VPN_TRACE_PCAP='/var/log/vpnsample/vpn-{side}.pcap' ./scripts/deploy-server.sh
# Writes one server-client-N file per connected client
```

For the automated flow, enable it while deploying and running:

```bash
VPN_TRACE_PACKETS=1 ./scripts/deploy-server.sh
VPN_TRACE_PACKETS=1 ./scripts/run-vpn.sh
ssh root@SERVER_IP journalctl -fu vpnsample.service
```

For direct `dotnet run` commands, preserve the setting across `sudo` explicitly:

```bash
sudo env VPN_TRACE_PACKETS=1 VPN_TLS_SERVER_NAME=vpn.twocubes.io \
  VPN_TLS_CERTIFICATE=/path/to/fullchain.pem VPN_TLS_PRIVATE_KEY=/path/to/privkey.pem \
  VPN_COVER_TOKEN=0123456789abcdef0123456789abcdef \
  dotnet run --project Server -- 443
sudo env VPN_TRACE_PACKETS=1 VPN_TLS_SERVER_NAME=vpn.twocubes.io \
  VPN_TLS_PINNED_CERTIFICATE=/path/to/server.crt \
  VPN_COVER_TOKEN=0123456789abcdef0123456789abcdef \
  dotnet run --project Client -- SERVER_IP 443
```

The relay path provides three profiles. `baseline` traces and forwards
each packet unchanged. `shuffle-split` additionally buffers up to three packets
for at most 5 ms, changes their order, and splits each into 256-byte tunnel
frames. The default `websocket-cover` uses 240-byte fragments and pads them to
64, 128, 256, 512, 1024, or 1440 bytes. Both peers must select the same profile:

```bash
VPN_PROFILE=websocket-cover ./scripts/deploy-server.sh
VPN_PROFILE=websocket-cover ./scripts/run-vpn.sh
```

Packet splitting here is explicit tunnel-frame fragmentation, not a sequence of
smaller TCP writes. Packet shuffling is deliberate IP-packet reordering; TCP
inside the tunnel can recover through its own sequence numbers, while protocols
that depend on UDP arrival order may observe the change.

These transformations apply to WSS relay/exit traffic. The primary peer path is
packet-preserving encrypted UDP. They do not guarantee DPI evasion: a
network observer can still fingerprint TLS behavior and see the destination,
traffic volume, and timing.

These modes trace the raw IP packets at each TUN boundary. Address changes
performed later by server NAT are outside the application and require an
additional capture on the server's public interface. Hex output and PCAP files
contain complete payloads and may expose sensitive data or consume substantial
storage, so enable them only while diagnosing traffic.

All clients are placed in the same overlay subnets. Client zero receives
`10.8.0.2` and `fd42:8::2`, client one receives `10.8.0.3` and `fd42:8::3`,
and both immediately get connected routes to `10.8.0.0/24` and `fd42:8::/64`.
The server owns `.1`/`::1`, supports 253 simultaneous clients, and reuses an
address after its client disconnects.

To send all client traffic through it, first preserve a direct route to the VPN
server (substitute the values from `ip route`), then replace the default route:

```bash
ip route
sudo ip route add SERVER_IP via ORIGINAL_GATEWAY dev ORIGINAL_INTERFACE
sudo ip route replace default dev svpn0
sudo ip -6 route replace default dev svpn0
```

Stopping the client closes and removes `svpn0`. Restore the original default route
if the operating system does not do so automatically:

```bash
sudo ip route replace default via ORIGINAL_GATEWAY dev ORIGINAL_INTERFACE
sudo ip -6 route replace ORIGINAL_IPV6_DEFAULT_ROUTE
```

Remove the server NAT rule after the experiment by repeating its `iptables` command
with `-D POSTROUTING` instead of `-A POSTROUTING`.

## How the code is split

- `Client/Program.cs` and `Server/Program.cs` compose WSS coordination/relay,
  direct UDP mesh, DNS, TUN, and the selected relay pipeline.
- `Protocol/IPacketEndpoint.cs` is the OS-neutral boundary consumed by the protocol.
- `Protocol/TunnelNetwork.cs` contains the tunnel port, networks, interface names,
  prefix lengths, and address assignment rule.
- `Protocol/TunnelPipeline.cs` pumps frames in both directions and applies
  `ITunnelStage` decorators in forward/reverse order.
- `Protocol/TunnelFrame.cs` provides stable packet and fragment metadata.
- `Protocol/Codecs/LengthPrefixedCodec.cs` owns the baseline wire format behind
  the `IWireCodec` strategy boundary.
- `Protocol/Profiles/TunnelProfileFactory.cs` composes the `baseline`,
  `shuffle-split`, and `websocket-cover` demonstration profiles.
- `Protocol/Stages/PacketShuffleStage.cs` reorders small outbound packet windows
  and flushes sparse traffic after a short bounded delay.
- `Protocol/Stages/FragmentStage.cs` splits and reassembles tunnel frames.
- `Protocol/Stages/PaddingStage.cs` hides exact fragment lengths behind buckets.
- `Dns/` is a separate assembly containing node registration, the in-memory
  lease registry, and the authoritative UDP DNS server for `.vpn`.
- `Mesh/` contains the coordination protocol, persistent P-256 identity,
  pairwise AES-GCM datagrams, replay protection, UDP rendezvous, path maintenance,
  direct overlay routing, and relay fallback endpoint.
- `Protocol/WebSocketTunnelTransport.cs` creates the pinned, authenticated WSS
  client while `WebSocketDuplexStream.cs` adapts WebSocket messages to the
  pipeline's existing `Stream` boundary.
- `Protocol/Stages/PacketTraceStage.cs`, `PacketTrace.cs`,
  `IpPacketFormatter.cs`, and `PcapWriter.cs` implement packet observation
  independently of framing and transport orchestration.
- `Os.Linux/LinuxTunDevice.cs` implements the packet endpoint using `/dev/net/tun`,
  Linux ioctls, and `ip` commands. It contains no TCP or packet-framing code.
- `Protocol.Tests/` verifies frame validation, codec round trips, handshake
  compatibility, and bidirectional stage ordering without requiring a TUN device.
- `Mesh.Tests/` proves crypto interoperability, replay rejection, identity
  persistence, control framing, direct UDP delivery, and relay fallback.

This boundary allows the protocol to run against an in-memory endpoint in tests
and allows another OS backend to be added without changing the wire protocol.
