#!/usr/bin/env bash
set -Eeuo pipefail

port=$1
ipv4_network=$2
ipv6_network=$3
trace_packets=$4
trace_hex=$5
trace_pcap=${6-}
[[ $trace_pcap == - ]] && trace_pcap=
profile=${7-baseline}
tls_server_name=$8
export DEBIAN_FRONTEND=noninteractive

apt-get update -qq
apt-get install -y -qq ca-certificates curl iproute2 iptables iputils-ping >/dev/null

dotnet_dir=/opt/vpnsample/dotnet
if ! "$dotnet_dir/dotnet" --list-runtimes 2>/dev/null | grep -q '^Microsoft.NETCore.App 10\.'; then
  mkdir -p "$dotnet_dir"
  curl -fsSL https://dot.net/v1/dotnet-install.sh -o /tmp/vpnsample-dotnet-install.sh
  bash /tmp/vpnsample-dotnet-install.sh --channel 10.0 --runtime dotnet \
    --install-dir "$dotnet_dir" >/dev/null
  rm -f /tmp/vpnsample-dotnet-install.sh
fi

out_interface=$(ip -4 route show default \
  | awk 'NR == 1 { for (i = 1; i <= NF; i++) if ($i == "dev") { print $(i + 1); exit } }')
test -n "$out_interface"
ip -6 route show default | grep -q .
ip -6 address show dev "$out_interface" scope global | grep -q 'inet6 '

printf 'VPN_OUT_INTERFACE=%s\nVPN_PORT=%s\nVPN_IPV4_NETWORK=%s\nVPN_IPV6_NETWORK=%s\nVPN_TRACE_PACKETS=%s\nVPN_TRACE_HEX=%s\nVPN_TRACE_PCAP=%s\nVPN_PROFILE=%s\nVPN_TLS_SERVER_NAME=%s\nVPN_TLS_CERTIFICATE=/etc/vpnsample/tls.crt\nVPN_TLS_PRIVATE_KEY=/etc/vpnsample/tls.key\n' \
  "$out_interface" "$port" "$ipv4_network" "$ipv6_network" "$trace_packets" \
  "$trace_hex" "$trace_pcap" "$profile" "$tls_server_name" \
  >/etc/default/vpnsample

cat >/etc/systemd/system/vpnsample.service <<'UNIT'
[Unit]
Description=VPNSample learning VPN server
After=network-online.target
Wants=network-online.target

[Service]
Type=simple
EnvironmentFile=/etc/default/vpnsample
ExecStartPre=/usr/sbin/sysctl -w net.ipv4.ip_forward=1
ExecStartPre=/usr/sbin/sysctl -w net.ipv6.conf.all.forwarding=1
ExecStartPre=/bin/sh -c '/usr/sbin/iptables -t nat -C POSTROUTING -s "$VPN_IPV4_NETWORK" -o "$VPN_OUT_INTERFACE" -j MASQUERADE || /usr/sbin/iptables -t nat -A POSTROUTING -s "$VPN_IPV4_NETWORK" -o "$VPN_OUT_INTERFACE" -j MASQUERADE'
ExecStartPre=/bin/sh -c '/usr/sbin/ip6tables -t nat -C POSTROUTING -s "$VPN_IPV6_NETWORK" -o "$VPN_OUT_INTERFACE" -j MASQUERADE || /usr/sbin/ip6tables -t nat -A POSTROUTING -s "$VPN_IPV6_NETWORK" -o "$VPN_OUT_INTERFACE" -j MASQUERADE'
ExecStart=/opt/vpnsample/dotnet/dotnet /opt/vpnsample/app/Server.dll ${VPN_PORT}
ExecStopPost=/bin/sh -c '/usr/sbin/iptables -t nat -D POSTROUTING -s "$VPN_IPV4_NETWORK" -o "$VPN_OUT_INTERFACE" -j MASQUERADE || true'
ExecStopPost=/bin/sh -c '/usr/sbin/ip6tables -t nat -D POSTROUTING -s "$VPN_IPV6_NETWORK" -o "$VPN_OUT_INTERFACE" -j MASQUERADE || true'
Restart=no

[Install]
WantedBy=multi-user.target
UNIT

if command -v ufw >/dev/null 2>&1 && ufw status | grep -q '^Status: active'; then
  ufw allow "$port/tcp" >/dev/null
fi

systemctl daemon-reload
systemctl enable vpnsample.service >/dev/null
systemctl restart vpnsample.service
