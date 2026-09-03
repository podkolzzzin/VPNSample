#!/usr/bin/env bash
set -Eeuo pipefail

SCRIPT_DIR=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)
PROJECT_ROOT=$(cd -- "$SCRIPT_DIR/.." && pwd)
VPNSAMPLE_LOG_PREFIX=run-vpn
# shellcheck source=lib/common.sh
source "$SCRIPT_DIR/lib/common.sh"
# shellcheck source=lib/routes.sh
source "$SCRIPT_DIR/lib/routes.sh"
STATE_FILE=${VPN_STATE_FILE:-$PROJECT_ROOT/.vpn-droplet.env}
CLIENT_PROJECT=$PROJECT_ROOT/Client/Client.csproj
CLIENT_DLL=$PROJECT_ROOT/Client/bin/Release/net10.0/Client.dll
VPN_ROUTE_METRIC=${VPN_ROUTE_METRIC:-50}
VPN_TRACE_PACKETS=${VPN_TRACE_PACKETS:-0}
VPN_TRACE_HEX=${VPN_TRACE_HEX:-0}
VPN_TRACE_PCAP=${VPN_TRACE_PCAP:-}
VPN_PROFILE=${VPN_PROFILE:-baseline}
VPN_TLS_SERVER_NAME=${VPN_TLS_SERVER_NAME:-}
VPN_TLS_PINNED_CERTIFICATE=${VPN_TLS_PINNED_CERTIFICATE:-}
peer_only=false

usage() {
  cat <<'EOF'
Usage: scripts/run-vpn.sh [--peer-only] [--state-file PATH]

Start the local VPN client using the droplet IP and port from the state file.
By default, the script routes IPv4 and IPv6 traffic through the tunnel
while preserving a direct IPv4 route to the server, then restores the original
routes when the client exits or Ctrl-C is pressed.

Options:
  --peer-only        Create the tunnel without replacing the default route
  --state-file PATH  State file location
  -h, --help         Show this help

Environment:
  VPN_ROUTE_METRIC   Priority for VPN default routes (default: 50)
  VPN_TRACE_PACKETS  Set to 1 for compact packet summaries
  VPN_TRACE_HEX      Set to 1 to add a multiline hexadecimal dump
  VPN_TRACE_PCAP     Base path for a Wireshark-compatible capture
  VPN_PROFILE        Tunnel pipeline profile (default: baseline)
  VPN_TLS_SERVER_NAME  HTTPS SNI/Host name (state file default: vpn.twocubes.io)
  VPN_TLS_PINNED_CERTIFICATE  Optional pinned server certificate
EOF
}

while (($#)); do
  case $1 in
    --peer-only) peer_only=true; shift ;;
    --state-file) STATE_FILE=${2:?Missing value for --state-file}; shift 2 ;;
    -h|--help) usage; exit 0 ;;
    *) fail "Unknown option: $1 (use --help)" ;;
  esac
done

need_all dotnet ip ping
[[ -f $CLIENT_PROJECT ]] || fail "Client project not found: $CLIENT_PROJECT"
load_state "$STATE_FILE"
: "${DROPLET_IP:?DROPLET_IP is missing from $STATE_FILE}"
VPN_PORT=${VPN_PORT:-443}
VPN_TLS_SERVER_NAME=${VPN_TLS_SERVER_NAME:-vpn.twocubes.io}
[[ $VPN_ROUTE_METRIC =~ ^[0-9]+$ ]] && ((VPN_ROUTE_METRIC >= 1 && VPN_ROUTE_METRIC <= 4294967295)) \
  || fail "VPN_ROUTE_METRIC must be an integer from 1 to 4294967295."
validate_port "$VPN_PORT"
validate_boolean VPN_TRACE_PACKETS "$VPN_TRACE_PACKETS"
validate_boolean VPN_TRACE_HEX "$VPN_TRACE_HEX"
validate_profile "$VPN_PROFILE"

if ((EUID != 0)); then
  need sudo
  dotnet_path=$(command -v dotnet)
  log "Building the VPN client as the current user..."
  "$dotnet_path" build "$CLIENT_PROJECT" -c Release
  log "Root privileges are required for TUN and route configuration; invoking sudo..."
  sudo_args=()
  [[ $peer_only == true ]] && sudo_args+=(--peer-only)
  exec sudo env \
    "PATH=$PATH" \
    "VPN_STATE_FILE=$STATE_FILE" \
    "VPN_DOTNET=$dotnet_path" \
    "VPN_ROUTE_METRIC=$VPN_ROUTE_METRIC" \
    "VPN_TRACE_PACKETS=$VPN_TRACE_PACKETS" \
    "VPN_TRACE_HEX=$VPN_TRACE_HEX" \
    "VPN_TRACE_PCAP=$VPN_TRACE_PCAP" \
    "VPN_PROFILE=$VPN_PROFILE" \
    "VPN_TLS_SERVER_NAME=$VPN_TLS_SERVER_NAME" \
    "VPN_TLS_PINNED_CERTIFICATE=$VPN_TLS_PINNED_CERTIFICATE" \
    "$0" "${sudo_args[@]}"
fi

dotnet_bin=${VPN_DOTNET:-$(command -v dotnet)}
if [[ ! -f $CLIENT_DLL ]]; then
  log "Release client binary is missing; building it..."
  "$dotnet_bin" build "$CLIENT_PROJECT" -c Release
fi
read -r ipv4_route_probe ipv6_route_probe \
  < <("$dotnet_bin" "$CLIENT_DLL" --print-route-probes)
read -r server_ipv4 server_ipv6 \
  < <("$dotnet_bin" "$CLIENT_DLL" --print-server-addresses)
[[ -n $ipv4_route_probe && -n $ipv6_route_probe ]] \
  || fail "Client did not report its route probe addresses."
[[ -n $server_ipv4 && -n $server_ipv6 ]] \
  || fail "Client did not report the server's overlay addresses."
vpn_routes_capture "$DROPLET_IP"

client_pid=
routes_changed=false
cleanup() {
  exit_status=$?
  trap - EXIT INT TERM
  if [[ -n $client_pid ]]; then
    kill "$client_pid" 2>/dev/null || true
    wait "$client_pid" 2>/dev/null || true
  fi
  if [[ $routes_changed == true ]]; then
    log "Restoring original routes..."
    vpn_routes_restore "$DROPLET_IP" "$VPN_ROUTE_METRIC" || true
  fi
  log "VPN stopped."
  exit "$exit_status"
}
trap cleanup EXIT INT TERM

log "Connecting to https://$VPN_TLS_SERVER_NAME:$VPN_PORT/vpn using profile '$VPN_PROFILE'..."
env \
  "VPN_TLS_SERVER_NAME=$VPN_TLS_SERVER_NAME" \
  "VPN_TLS_PINNED_CERTIFICATE=$VPN_TLS_PINNED_CERTIFICATE" \
  "$dotnet_bin" "$CLIENT_DLL" "$DROPLET_IP" "$VPN_PORT" &
client_pid=$!

for attempt in $(seq 1 30); do
  if ip link show svpn0 >/dev/null 2>&1; then
    break
  fi
  kill -0 "$client_pid" 2>/dev/null || fail "VPN client exited before creating svpn0."
  ((attempt < 30)) || fail "Timed out waiting for svpn0."
  sleep 1
done

client_ipv6=$(ip -o -6 address show dev svpn0 scope global \
  | awk '{ split($4, address, "/"); print address[1]; exit }')
[[ -n $client_ipv6 ]] || fail "Could not read the assigned IPv6 address from svpn0."

log "Tunnel is up. Testing IPv4 and IPv6 peers..."
ping -c 1 -W 3 "$server_ipv4" >/dev/null || fail "IPv4 tunnel peer did not answer."
ping -6 -c 1 -W 3 "$server_ipv6" >/dev/null || fail "IPv6 tunnel peer did not answer."
log "IPv4 and IPv6 peer tests passed."

if [[ $peer_only == false ]]; then
  log "Preserving the direct server route and switching IPv4/IPv6 default routes..."
  routes_changed=true
  vpn_routes_apply "$DROPLET_IP" "$VPN_ROUTE_METRIC"

  selected_ipv4_interface=$(route_interface 4 "$ipv4_route_probe")
  selected_ipv6_interface=$(route_interface 6 "$ipv6_route_probe")
  [[ $selected_ipv4_interface == svpn0 ]] \
    || fail "IPv4 leak check failed: traffic still selects $selected_ipv4_interface."
  [[ $selected_ipv6_interface == svpn0 ]] \
    || fail "IPv6 leak check failed: traffic still selects $selected_ipv6_interface."
  log "Route leak checks passed: IPv4 and IPv6 both select svpn0 (metric $VPN_ROUTE_METRIC)."
  log "All IPv4 and IPv6 traffic now uses the VPN. Press Ctrl-C to disconnect and restore routes."
else
  log "Peer-only mode is active. The overlay subnet uses svpn0; internet traffic keeps its original route. Press Ctrl-C to stop."
fi

set +e
wait "$client_pid"
client_status=$?
set -e
client_pid=
exit "$client_status"
