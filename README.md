# Simple IP tunnel (.NET 10 / C#)

A small Linux-only learning project. Every client gets a dual-stack TUN interface
in one shared overlay subnet and exchanges raw IPv4 and IPv6 packets with the server
over its own TCP connection. The server uses one TUN as an IPv4/IPv6 exit node and
routes client-to-client packets in .NET.

Current architecture, deployment flow, and IPv4/IPv6 routing diagrams are in
[ARCHITECTURE.md](ARCHITECTURE.md).

The wire transport uses TLS 1.2/1.3 plus an HTTP/1.1 Upgrade at
`https://vpn.twocubes.io/vpn`. The automated demo pins a temporary certificate,
but clients do not authenticate themselves to the server yet. This remains a
learning project rather than a production VPN.

## Requirements

- Linux on both machines
- .NET 10 SDK
- `iproute2`; `iptables` for internet forwarding; `openssl` for demo certificates
- root privileges for `/dev/net/tun` and network configuration
- TCP port 443 allowed through the server firewall

## Build

From this directory:

```bash
dotnet build VPNSample.slnx
```

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
forwarding and NAT, creates a seven-day certificate for `vpn.twocubes.io`, and
starts `vpnsample.service`. The public certificate is recorded in the state file
and pinned by `run-vpn.sh`; its private key exists only on the server.

The temporary flow connects to the droplet IP while sending `vpn.twocubes.io`
as TLS SNI and HTTP `Host`, so it does not require a DNS change. For a permanent
deployment, point the `vpn.twocubes.io` A/AAAA records at the server, issue a
publicly trusted certificate, and provide its PEM files to `deploy-server.sh`:

```bash
VPN_TLS_CERTIFICATE=/path/to/fullchain.pem \
VPN_TLS_PRIVATE_KEY=/path/to/privkey.pem \
./scripts/deploy-server.sh
```

This leaves the existing `twocubes.io` website untouched. On the wire the
outer connection is real TLS on TCP/443; the tunnel frames and HTTP Upgrade are
encrypted. A network observer can still see the destination IP and normally the
TLS SNI, so the DNS record and server address should agree in a permanent setup.

By default, `run-vpn.sh` replaces the local IPv4 and IPv6 default routes while the
client is running and restores them on exit. Use `--peer-only` to create and test
the tunnel without changing either default route. The VPN IPv6 route uses metric
50 so it wins over typical router-advertisement routes; override this with
`VPN_ROUTE_METRIC` if the host has a still-lower-priority metric.

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
ping, confirms TLS plus HTTP Upgrade on both clients, checks internet
reachability through the exit node, fetches nginx over both
tunnel address families, checks nginx's access log for the requester's overlay
addresses, and deletes all temporary resources on exit. The regions and droplet
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
  dotnet run --project Server -- 443
```

## Run the client

Connect using the server address:

```bash
sudo env \
  VPN_TLS_SERVER_NAME=vpn.twocubes.io \
  VPN_TLS_PINNED_CERTIFICATE=/path/to/server.crt \
  dotnet run --project Client -- SERVER_IP 443
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
  dotnet run --project Server -- 443
sudo env VPN_TRACE_PACKETS=1 VPN_TLS_SERVER_NAME=vpn.twocubes.io \
  VPN_TLS_PINNED_CERTIFICATE=/path/to/server.crt \
  dotnet run --project Client -- SERVER_IP 443
```

The core architecture currently provides only the no-tricks `baseline` profile.
Both peers must select the same profile. The automation scripts default to
`VPN_PROFILE=baseline`; future profiles can be selected while deploying and
running without changing the client or server composition roots.

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

- `Client/Program.cs` and `Server/Program.cs` open a TCP connection, create a TUN
  endpoint, select the baseline profile, and run its tunnel pipeline.
- `Protocol/IPacketEndpoint.cs` is the OS-neutral boundary consumed by the protocol.
- `Protocol/TunnelNetwork.cs` contains the tunnel port, networks, interface names,
  prefix lengths, and address assignment rule.
- `Protocol/TunnelPipeline.cs` pumps frames in both directions and applies
  `ITunnelStage` decorators in forward/reverse order.
- `Protocol/TunnelFrame.cs` provides stable packet and fragment metadata for
  future transformations.
- `Protocol/Codecs/LengthPrefixedCodec.cs` owns the baseline wire format behind
  the `IWireCodec` strategy boundary.
- `Protocol/Profiles/TunnelProfileFactory.cs` is the composition point for the
  current baseline and future demonstration profiles.
- `Protocol/Stages/PacketTraceStage.cs`, `PacketTrace.cs`,
  `IpPacketFormatter.cs`, and `PcapWriter.cs` implement packet observation
  independently of framing and transport orchestration.
- `Os.Linux/LinuxTunDevice.cs` implements the packet endpoint using `/dev/net/tun`,
  Linux ioctls, and `ip` commands. It contains no TCP or packet-framing code.
- `Protocol.Tests/` verifies frame validation, codec round trips, handshake
  compatibility, and bidirectional stage ordering without requiring a TUN device.

This boundary allows the protocol to run against an in-memory endpoint in tests
and allows another OS backend to be added without changing the wire protocol.
