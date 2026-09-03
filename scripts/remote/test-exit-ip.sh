#!/usr/bin/env bash
set -Eeuo pipefail

SCRIPT_DIR=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)
VPNSAMPLE_LOG_PREFIX=e2e-vpn-iptest
# shellcheck source=../lib/common.sh
source "$SCRIPT_DIR/lib/common.sh"
# shellcheck source=../lib/routes.sh
source "$SCRIPT_DIR/lib/routes.sh"

status_file=$1
server_ipv4=$2
server_ipv6=$3
vpn_port=$4
dotnet_bin=$5
client_dll=$6
tls_server_name=$7
pinned_certificate=${8:-}
profile=${9:-shuffle-split}
route_metric=50
client_log=${status_file%/*}/vpn-client.log
client_pid=
routes_changed=false

normalize_ip() {
  python3 -c \
    'import ipaddress, sys; print(ipaddress.ip_address(sys.argv[1]))' "$1" 2>/dev/null
}

probe_external_ip() {
  local family=$1
  local url=$2
  local value=
  local normalized
  if value=$(curl "--ipv$family" --noproxy '*' --fail --silent --show-error \
      --location --connect-timeout 8 --max-time 20 --retry 2 --retry-all-errors \
      "$url" 2>/dev/null); then
    value=${value//$'\r'/}
    value=${value//$'\n'/}
    if normalized=$(normalize_ip "$value") \
      && [[ $normalized == *:* && $family == 6 \
        || $normalized != *:* && $family == 4 ]]; then
      printf '%s\n' "$normalized"
      return
    fi
  fi
  printf '<unavailable>\n'
}

capture_external_ips() {
  local prefix=$1
  printf -v "${prefix}_v4_ipify" '%s' \
    "$(probe_external_ip 4 https://api4.ipify.org)"
  printf -v "${prefix}_v4_icanhazip" '%s' \
    "$(probe_external_ip 4 https://ipv4.icanhazip.com)"
  printf -v "${prefix}_v6_ipify" '%s' \
    "$(probe_external_ip 6 https://api6.ipify.org)"
  printf -v "${prefix}_v6_icanhazip" '%s' \
    "$(probe_external_ip 6 https://ipv6.icanhazip.com)"
}

print_external_ips() {
  local prefix=$1
  local v4_ipify_name=${prefix}_v4_ipify
  local v4_icanhazip_name=${prefix}_v4_icanhazip
  local v6_ipify_name=${prefix}_v6_ipify
  local v6_icanhazip_name=${prefix}_v6_icanhazip
  printf 'External IPv4 (api4.ipify.org):      %s\n' "${!v4_ipify_name}"
  printf 'External IPv4 (ipv4.icanhazip.com): %s\n' "${!v4_icanhazip_name}"
  printf 'External IPv6 (api6.ipify.org):      %s\n' "${!v6_ipify_name}"
  printf 'External IPv6 (ipv6.icanhazip.com): %s\n' "${!v6_icanhazip_name}"
}

expected_seen() {
  local expected
  expected=$(normalize_ip "$1") || return 1
  shift
  local observed
  for observed in "$@"; do
    [[ $observed == "$expected" ]] && return
  done
  return 1
}

cleanup() {
  local status=$?
  trap - EXIT INT TERM
  if [[ -n $client_pid ]]; then
    kill "$client_pid" 2>/dev/null || true
    wait "$client_pid" 2>/dev/null || true
  fi
  if [[ $routes_changed == true ]]; then
    log "Restoring the probe droplet's original routes..."
    vpn_routes_restore "$server_ipv4" "$route_metric" || status=1
  fi
  printf '%s\n' "$status" >"$status_file"
  exit "$status"
}
trap cleanup EXIT INT TERM

need_all awk curl ifconfig ip ping python3
[[ -x $dotnet_bin ]] || fail "Remote dotnet runtime not found: $dotnet_bin"
[[ -f $client_dll ]] || fail "Remote VPN client not found: $client_dll"
[[ ! -e /sys/class/net/svpn0 ]] || fail "svpn0 already exists on the probe droplet."

printf '\n===== BEFORE VPN: ifconfig =====\n'
ifconfig
printf '\n===== BEFORE VPN: external services =====\n'
capture_external_ips before
print_external_ips before

vpn_routes_capture "$server_ipv4"
read -r server_tunnel_v4 server_tunnel_v6 \
  < <("$dotnet_bin" "$client_dll" --print-server-addresses)

log "Connecting the probe to https://$tls_server_name:$vpn_port/vpn..."
env VPN_TLS_SERVER_NAME="$tls_server_name" \
  VPN_TLS_PINNED_CERTIFICATE="$pinned_certificate" \
  VPN_PROFILE="$profile" \
  "$dotnet_bin" "$client_dll" "$server_ipv4" "$vpn_port" >"$client_log" 2>&1 &
client_pid=$!

for attempt in $(seq 1 30); do
  [[ -e /sys/class/net/svpn0 ]] && break
  kill -0 "$client_pid" 2>/dev/null \
    || { sed -n '1,200p' "$client_log"; fail "VPN client exited before creating svpn0."; }
  ((attempt < 30)) || fail "Timed out waiting for svpn0."
  sleep 1
done

ping -c 1 -W 3 "$server_tunnel_v4" >/dev/null
ping -6 -c 1 -W 3 "$server_tunnel_v6" >/dev/null
routes_changed=true
vpn_routes_apply "$server_ipv4" "$route_metric"
[[ $(route_interface 4 1.1.1.1) == svpn0 ]] \
  || fail "IPv4 default route did not select svpn0."
[[ $(route_interface 6 2606:4700:4700::1111) == svpn0 ]] \
  || fail "IPv6 default route did not select svpn0."

printf '\n===== VPN CLIENT LOG =====\n'
sed -n '1,200p' "$client_log"
printf '\n===== DURING VPN: ifconfig =====\n'
ifconfig
printf '\n===== DURING VPN: external services =====\n'
capture_external_ips during
print_external_ips during
printf 'Expected VPN-server IPv4:             %s\n' "$server_ipv4"
printf 'Expected VPN-server IPv6:             %s\n' "$server_ipv6"

expected_seen "$server_ipv4" "$during_v4_ipify" "$during_v4_icanhazip" \
  || fail "Neither IPv4 service observed the VPN server's public IPv4 address."
expected_seen "$server_ipv6" "$during_v6_ipify" "$during_v6_icanhazip" \
  || fail "Neither IPv6 service observed the VPN server's public IPv6 address."

kill "$client_pid" 2>/dev/null || true
wait "$client_pid" 2>/dev/null || true
client_pid=
vpn_routes_restore "$server_ipv4" "$route_metric"
routes_changed=false

printf '\n===== AFTER VPN: ifconfig =====\n'
ifconfig
printf '\n===== AFTER VPN: external services =====\n'
capture_external_ips after
print_external_ips after

printf '\n===== RESULT =====\n'
printf 'PASS: svpn0 carried both IPv4 and IPv6 traffic through the VPN server.\n'
printf 'IPv4 before: %s | during: %s | after: %s\n' \
  "$before_v4_ipify" "$during_v4_ipify" "$after_v4_ipify"
printf 'IPv6 before: %s | during: %s | after: %s\n' \
  "$before_v6_ipify" "$during_v6_ipify" "$after_v6_ipify"
