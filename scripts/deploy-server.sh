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
VPN_PROFILE=${VPN_PROFILE:-websocket-cover}
VPN_COVER_TOKEN=${VPN_COVER_TOKEN:-}
VPN_TLS_SERVER_NAME=${VPN_TLS_SERVER_NAME:-vpn.twocubes.io}
VPN_TLS_CERTIFICATE=${VPN_TLS_CERTIFICATE:-}
VPN_TLS_PRIVATE_KEY=${VPN_TLS_PRIVATE_KEY:-}
VPN_TLS_PINNED_CERTIFICATE=${VPN_TLS_PINNED_CERTIFICATE:-}

usage() {
  cat <<'EOF'
Usage: scripts/deploy-server.sh [--state-file PATH]

Publish the VPN server, install its runtime and networking prerequisites on the
recorded droplet, and start it as vpnsample.service.

Environment: VPN_STATE_FILE, SSH_USER (default: root), VPN_TRACE_PACKETS,
VPN_TRACE_HEX, VPN_TRACE_PCAP, VPN_PROFILE (default: websocket-cover), and optional
SSH_KNOWN_HOSTS_FILE. HTTPS uses VPN_TLS_SERVER_NAME (default:
vpn.twocubes.io). Set both VPN_TLS_CERTIFICATE and VPN_TLS_PRIVATE_KEY to deploy
an existing PEM certificate; otherwise a temporary pinned certificate is made.
VPN_COVER_TOKEN can provide a stable WebSocket access token; otherwise one is
generated and saved in the state file.
EOF
}

while (($#)); do
  case $1 in
    --state-file) STATE_FILE=${2:?Missing value for --state-file}; shift 2 ;;
    -h|--help) usage; exit 0 ;;
    *) fail "Unknown option: $1 (use --help)" ;;
  esac
done

need_all curl dotnet ssh scp openssl
[[ -f $SERVER_PROJECT ]] || fail "Server project not found: $SERVER_PROJECT"
[[ -f $REMOTE_SERVER_SETUP ]] || fail "Remote setup script not found: $REMOTE_SERVER_SETUP"
load_state "$STATE_FILE"
: "${DROPLET_IP:?DROPLET_IP is missing from $STATE_FILE}"
: "${SSH_KEY_PATH:?SSH_KEY_PATH is missing from $STATE_FILE}"
VPN_PORT=${VPN_PORT:-443}
[[ -f $SSH_KEY_PATH ]] || fail "SSH private key not found: $SSH_KEY_PATH"
validate_port "$VPN_PORT"
validate_boolean VPN_TRACE_PACKETS "$VPN_TRACE_PACKETS"
validate_boolean VPN_TRACE_HEX "$VPN_TRACE_HEX"
[[ $VPN_TRACE_PCAP != *[[:space:]]* ]] \
  || fail "VPN_TRACE_PCAP must not contain whitespace."
validate_profile "$VPN_PROFILE"
if [[ -z $VPN_COVER_TOKEN ]]; then
  VPN_COVER_TOKEN=$(openssl rand -hex 24)
fi
validate_cover_token "$VPN_COVER_TOKEN"
validate_dns_name "$VPN_TLS_SERVER_NAME"
[[ -z $VPN_TLS_CERTIFICATE && -z $VPN_TLS_PRIVATE_KEY \
  || -n $VPN_TLS_CERTIFICATE && -n $VPN_TLS_PRIVATE_KEY ]] \
  || fail "Set both VPN_TLS_CERTIFICATE and VPN_TLS_PRIVATE_KEY, or neither."

ssh_options_for "$SSH_KEY_PATH" "$SSH_KNOWN_HOSTS_FILE"
remote=$SSH_USER@$DROPLET_IP
publish_dir=$(mktemp -d)
trap 'rm -rf -- "$publish_dir"' EXIT

managed_tls_certificate=false
tls_certificate_source=$VPN_TLS_CERTIFICATE
tls_private_key_source=$VPN_TLS_PRIVATE_KEY
if [[ -z $tls_certificate_source ]]; then
  managed_tls_certificate=true
  tls_certificate_source=$publish_dir/tls.crt
  tls_private_key_source=$publish_dir/tls.key
  log "Generating a temporary pinned TLS certificate for $VPN_TLS_SERVER_NAME..."
  openssl req -x509 -newkey rsa:2048 -sha256 -nodes -days 7 \
    -subj "/CN=$VPN_TLS_SERVER_NAME" \
    -addext "subjectAltName=DNS:$VPN_TLS_SERVER_NAME" \
    -keyout "$tls_private_key_source" \
    -out "$tls_certificate_source" >/dev/null 2>&1
else
  [[ -f $tls_certificate_source ]] \
    || fail "TLS certificate not found: $tls_certificate_source"
  [[ -f $tls_private_key_source ]] \
    || fail "TLS private key not found: $tls_private_key_source"
fi

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
  'rm -rf /opt/vpnsample/app && mkdir -p /opt/vpnsample/app /etc/vpnsample'
scp "${SSH_OPTIONS[@]}" -r "$publish_dir/server/." "$remote:/opt/vpnsample/app/"
scp "${SSH_OPTIONS[@]}" "$tls_certificate_source" "$remote:/etc/vpnsample/tls.crt"
scp "${SSH_OPTIONS[@]}" "$tls_private_key_source" "$remote:/etc/vpnsample/tls.key"
ssh "${SSH_OPTIONS[@]}" "$remote" \
  'chmod 644 /etc/vpnsample/tls.crt && chmod 600 /etc/vpnsample/tls.key'

log "Installing .NET 10 and configuring forwarding, NAT, and systemd..."
ssh "${SSH_OPTIONS[@]}" "$remote" bash -s -- \
  "$VPN_PORT" "$ipv4_network" "$ipv6_network" "$VPN_TRACE_PACKETS" \
  "$VPN_TRACE_HEX" "${VPN_TRACE_PCAP:--}" "$VPN_PROFILE" "$VPN_TLS_SERVER_NAME" \
  "$VPN_COVER_TOKEN" \
  <"$REMOTE_SERVER_SETUP"

ssh "${SSH_OPTIONS[@]}" "$remote" systemctl is-active --quiet vpnsample.service \
  || fail "Server did not start. Inspect: ssh $remote journalctl -u vpnsample"

cover_url="https://$VPN_TLS_SERVER_NAME:$VPN_PORT/"
log "Verifying the HTTPS cover page at $cover_url..."
curl --noproxy '*' --fail --silent --show-error \
  --resolve "$VPN_TLS_SERVER_NAME:$VPN_PORT:$DROPLET_IP" \
  --cacert "$tls_certificate_source" "$cover_url" \
  | grep -Fq '<title>Two Cubes' \
  || fail "The HTTPS cover page did not return the expected content."
probe_status=$(curl --noproxy '*' --silent --output /dev/null --write-out '%{http_code}' \
  --resolve "$VPN_TLS_SERVER_NAME:$VPN_PORT:$DROPLET_IP" \
  --cacert "$tls_certificate_source" \
  "https://$VPN_TLS_SERVER_NAME:$VPN_PORT/api/v1/events")
[[ $probe_status == 404 ]] \
  || fail "The unauthenticated tunnel probe returned HTTP $probe_status instead of 404."

if [[ $managed_tls_certificate == true ]]; then
  VPN_TLS_PINNED_CERTIFICATE=${STATE_FILE}.tls.crt
  cp -- "$tls_certificate_source" "$VPN_TLS_PINNED_CERTIFICATE"
fi
{
  printf 'VPN_TLS_SERVER_NAME=%q\n' "$VPN_TLS_SERVER_NAME"
  printf 'VPN_TLS_PINNED_CERTIFICATE=%q\n' "$VPN_TLS_PINNED_CERTIFICATE"
  printf 'MANAGED_TLS_CERTIFICATE=%q\n' "$managed_tls_certificate"
  printf 'VPN_COVER_TOKEN=%q\n' "$VPN_COVER_TOKEN"
} >>"$STATE_FILE"

log "HTTPS cover site and protected WebSocket tunnel are listening on $DROPLET_IP:$VPN_PORT"
log "Next: $PROJECT_ROOT/scripts/run-vpn.sh"
log "After testing: $PROJECT_ROOT/scripts/create-droplet.sh --delete"
