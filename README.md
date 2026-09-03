# Simple IP tunnel (.NET 10 / C#)

A small Linux-only learning project. Every client gets a dual-stack TUN interface
and exchanges raw IPv4 and IPv6 packets with the server over its own TCP connection.

Current architecture, deployment flow, and IPv4/IPv6 routing diagrams are in
[ARCHITECTURE.md](ARCHITECTURE.md).

This is not a secure VPN. The TCP connection is unencrypted and unauthenticated.
Anyone who can reach the connection can read or change its traffic. The project is
only intended to demonstrate TUN devices, packet framing, routing, and forwarding.

## Requirements

- Linux on both machines
- .NET 10 SDK
- `iproute2`; `iptables` for internet forwarding
- root privileges for `/dev/net/tun` and network configuration
- TCP port 4433 allowed through the server firewall

## Build

From this directory:

```bash
dotnet build VPNSample.slnx
```

## Demo stages

This commit is `stage-01-basic-tunnel`. Move through the development history
with `./scripts/checkout_next_tag.sh` and `./scripts/checkout_prev_tag.sh`.
Both commands refuse to replace uncommitted work.

The automation layout is documented in [scripts/README.md](scripts/README.md).

## Automated DigitalOcean flow

The scripts require `doctl`, `ssh`, `scp`, `ssh-keygen`, and the .NET 10 SDK
locally. Authenticate `doctl` first.

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
forwarding and NAT, and starts `vpnsample.service`.

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

## Run the server

First enable IPv4 and IPv6 forwarding. Replace `eth0` with the server's internet
interface:

```bash
sudo sysctl -w net.ipv4.ip_forward=1
sudo sysctl -w net.ipv6.conf.all.forwarding=1
sudo iptables -t nat -A POSTROUTING -s 10.8.0.0/16 -o eth0 -j MASQUERADE
sudo ip6tables -t nat -A POSTROUTING -s fd42:8::/48 -o eth0 -j MASQUERADE
sudo dotnet run --project Server -- 4433
```

## Run the client

Connect using the server address:

```bash
sudo dotnet run --project Client -- SERVER_IP 4433
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
sudo env VPN_TRACE_PACKETS=1 dotnet run --project Server -- 4433
sudo env VPN_TRACE_PACKETS=1 dotnet run --project Client -- SERVER_IP 4433
```

These modes trace the raw IP packets at each TUN boundary. Address changes
performed later by server NAT are outside the application and require an
additional capture on the server's public interface. Hex output and PCAP files
contain complete payloads and may expose sensitive data or consume substantial
storage, so enable them only while diagnosing traffic.

The next client uses `10.8.1.2`, `10.8.1.1`, `fd42:8:1::2`, and
`fd42:8:1::1`. The server supports client numbers 0 through 255 and reuses a
number after that client disconnects.

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
  endpoint, and connect it to the packet protocol.
- `Protocol/IPacketEndpoint.cs` is the OS-neutral boundary consumed by the protocol.
- `Protocol/TunnelNetwork.cs` contains the tunnel port, networks, interface names,
  prefix lengths, and address assignment rule.
- `Protocol/PacketTunnelProtocol.cs` frames complete IP packets with a two-byte
  length and pumps them over any duplex `Stream`. It contains no Linux or network
  configuration code.
- `Protocol/PacketTrace.cs` renders summaries and hex dumps,
  `IpPacketFormatter.cs` decodes IP metadata, and `PcapWriter.cs` writes captures.
- `Os.Linux/LinuxTunDevice.cs` implements the packet endpoint using `/dev/net/tun`,
  Linux ioctls, and `ip` commands. It contains no TCP or packet-framing code.

This boundary allows the protocol to run against an in-memory endpoint in tests
and allows another OS backend to be added without changing the wire protocol.
