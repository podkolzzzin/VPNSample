#!/usr/bin/env bash
set -Eeuo pipefail

SCRIPT_DIR=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)
PROJECT_ROOT=$(cd -- "$SCRIPT_DIR/.." && pwd)
REMOTE_SERVER_SETUP=$SCRIPT_DIR/remote/configure-server.sh
VPNSAMPLE_LOG_PREFIX=deploy-server
# shellcheck source=lib/common.sh
source "$SCRIPT_DIR/lib/common.sh"
# shellcheck source=lib/ssh.sh
source "$SCRIPT_DIR/lib/ssh.sh"
STATE_FILE=${VPN_STATE_FILE:-$PROJECT_ROOT/.vpn-droplet.env}
SERVER_PROJECT=$PROJECT_ROOT/Server/Server.csproj
SSH_USER=${SSH_USER:-root}
SSH_KNOWN_HOSTS_FILE=${SSH_KNOWN_HOSTS_FILE:-}
VPN_TRACE_PACKETS=${VPN_TRACE_PACKETS:-0}
VPN_TRACE_HEX=${VPN_TRACE_HEX:-0}
VPN_TRACE_PCAP=${VPN_TRACE_PCAP:-}
VPN_PROFILE=${VPN_PROFILE:-baseline}

usage() {
  cat <<'EOF'
Usage: scripts/deploy-server.sh [--state-file PATH]

Publish the VPN server, install its runtime and networking prerequisites on the
recorded droplet, and start it as vpnsample.service.

Environment: VPN_STATE_FILE, SSH_USER (default: root), VPN_TRACE_PACKETS,
VPN_TRACE_HEX, VPN_TRACE_PCAP, VPN_PROFILE (default: baseline), and optional
SSH_KNOWN_HOSTS_FILE.
EOF
}

while (($#)); do
  case $1 in
    --state-file) STATE_FILE=${2:?Missing value for --state-file}; shift 2 ;;
    -h|--help) usage; exit 0 ;;
    *) fail "Unknown option: $1 (use --help)" ;;
  esac
done

need_all dotnet ssh scp
[[ -f $SERVER_PROJECT ]] || fail "Server project not found: $SERVER_PROJECT"
[[ -f $REMOTE_SERVER_SETUP ]] || fail "Remote setup script not found: $REMOTE_SERVER_SETUP"
load_state "$STATE_FILE"
: "${DROPLET_IP:?DROPLET_IP is missing from $STATE_FILE}"
: "${SSH_KEY_PATH:?SSH_KEY_PATH is missing from $STATE_FILE}"
VPN_PORT=${VPN_PORT:-4433}
[[ -f $SSH_KEY_PATH ]] || fail "SSH private key not found: $SSH_KEY_PATH"
validate_port "$VPN_PORT"
validate_boolean VPN_TRACE_PACKETS "$VPN_TRACE_PACKETS"
validate_boolean VPN_TRACE_HEX "$VPN_TRACE_HEX"
[[ $VPN_TRACE_PCAP != *[[:space:]]* ]] \
  || fail "VPN_TRACE_PCAP must not contain whitespace."
validate_profile "$VPN_PROFILE"

ssh_options_for "$SSH_KEY_PATH" "$SSH_KNOWN_HOSTS_FILE"
remote=$SSH_USER@$DROPLET_IP
publish_dir=$(mktemp -d)
trap 'rm -rf -- "$publish_dir"' EXIT

log "Publishing the .NET 10 server..."
dotnet publish "$SERVER_PROJECT" -c Release --no-self-contained -o "$publish_dir/server"
read -r ipv4_network ipv6_network \
  < <(dotnet "$publish_dir/server/Server.dll" --print-networks)
[[ -n $ipv4_network && -n $ipv6_network ]] \
  || fail "Server did not report its tunnel networks."

log "Waiting for SSH on $DROPLET_IP..."
wait_for_ssh "$DROPLET_IP" "$SSH_KEY_PATH" "$SSH_USER" 30 10 \
  "$SSH_KNOWN_HOSTS_FILE"

log "Uploading server files..."
ssh "${SSH_OPTIONS[@]}" "$remote" \
  'rm -rf /opt/vpnsample/app && mkdir -p /opt/vpnsample/app'
scp "${SSH_OPTIONS[@]}" -r "$publish_dir/server/." "$remote:/opt/vpnsample/app/"

log "Installing .NET 10 and configuring forwarding, NAT, and systemd..."
ssh "${SSH_OPTIONS[@]}" "$remote" bash -s -- \
  "$VPN_PORT" "$ipv4_network" "$ipv6_network" "$VPN_TRACE_PACKETS" \
  "$VPN_TRACE_HEX" "${VPN_TRACE_PCAP:--}" "$VPN_PROFILE" \
  <"$REMOTE_SERVER_SETUP"

ssh "${SSH_OPTIONS[@]}" "$remote" systemctl is-active --quiet vpnsample.service \
  || fail "Server did not start. Inspect: ssh $remote journalctl -u vpnsample"

log "VPN server is listening on $DROPLET_IP:$VPN_PORT"
log "Next: $PROJECT_ROOT/scripts/run-vpn.sh"
log "After testing: $PROJECT_ROOT/scripts/create-droplet.sh --delete"
