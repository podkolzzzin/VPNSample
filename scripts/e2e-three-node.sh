#!/usr/bin/env bash
set -Eeuo pipefail

SCRIPT_DIR=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)
PROJECT_ROOT=$(cd -- "$SCRIPT_DIR/.." && pwd)
REMOTE_CLIENT_SETUP=$SCRIPT_DIR/remote/configure-client.sh
VPNSAMPLE_LOG_PREFIX=three-node-e2e
# shellcheck source=lib/common.sh
source "$SCRIPT_DIR/lib/common.sh"
# shellcheck source=lib/ssh.sh
source "$SCRIPT_DIR/lib/ssh.sh"
# shellcheck source=lib/digitalocean.sh
source "$SCRIPT_DIR/lib/digitalocean.sh"
# shellcheck source=lib/three-node-e2e.sh
source "$SCRIPT_DIR/lib/three-node-e2e.sh"
# shellcheck source=lib/three-node-checks.sh
source "$SCRIPT_DIR/lib/three-node-checks.sh"
CREATE_DROPLET=$SCRIPT_DIR/create-droplet.sh
DEPLOY_SERVER=$SCRIPT_DIR/deploy-server.sh
SERVER_REGION=${SERVER_REGION:-ams3}
CLIENT_A_REGION=${CLIENT_A_REGION:-fra1}
CLIENT_B_REGION=${CLIENT_B_REGION:-nyc3}
DO_SIZE=${DO_SIZE:-s-1vcpu-1gb}
VPN_PROFILE=${VPN_PROFILE:-websocket-cover}
VPN_DNS_ZONE=vpn
CLIENT_A_NAME=${CLIENT_A_NAME:-nginx-node}
CLIENT_B_NAME=${CLIENT_B_NAME:-requester-node}

usage() {
  cat <<'EOF'
Usage: scripts/e2e-three-node.sh

Create three temporary DigitalOcean droplets in distinct regions, run the VPN
server on one and VPN clients on the other two, and verify peer-to-peer IPv4,
IPv6, private DNS, and nginx-by-name traffic through the VPN. All resources are
removed when the script exits, including after a failed check or Ctrl-C.

Environment:
  SERVER_REGION    VPN server region (default: ams3)
  CLIENT_A_REGION  nginx client region (default: fra1)
  CLIENT_B_REGION  requester client region (default: nyc3)
  DO_SIZE          Droplet size (default: s-1vcpu-1gb)
  VPN_PROFILE      Tunnel pipeline profile (default: websocket-cover)
  CLIENT_A_NAME    DNS name of the nginx node (default: nginx-node)
  CLIENT_B_NAME    DNS name of the requester node (default: requester-node)

Prerequisites: authenticated doctl, dotnet 10, ssh, scp, and ssh-keygen.
This test creates billable DigitalOcean resources for the duration of the run.
EOF
}

if (($#)); then
  case $1 in
    -h|--help) usage; exit 0 ;;
    *) fail "Unknown option: $1 (use --help)" ;;
  esac
fi

need_all doctl dotnet ssh scp ssh-keygen
[[ -f $REMOTE_CLIENT_SETUP ]] \
  || fail "Remote setup script not found: $REMOTE_CLIENT_SETUP"
[[ $SERVER_REGION != "$CLIENT_A_REGION" \
  && $SERVER_REGION != "$CLIENT_B_REGION" \
  && $CLIENT_A_REGION != "$CLIENT_B_REGION" ]] \
  || fail "SERVER_REGION, CLIENT_A_REGION, and CLIENT_B_REGION must be distinct."
validate_profile "$VPN_PROFILE"
validate_node_name "$CLIENT_A_NAME"
validate_node_name "$CLIENT_B_NAME"
[[ ${CLIENT_A_NAME,,} != "${CLIENT_B_NAME,,}" ]] \
  || fail "CLIENT_A_NAME and CLIENT_B_NAME must be distinct."
doctl account get >/dev/null

work_dir=$(mktemp -d /tmp/vpnsample-three-node.XXXXXXXX)
server_state=$work_dir/server.env
client_a_state=$work_dir/client-a.env
client_b_state=$work_dir/client-b.env
publish_dir=$work_dir/client-publish
shared_key=$work_dir/shared-key
shared_key_id=
run_id=$(date -u +%Y%m%d-%H%M%S)

cleanup() {
  local status=$?
  trap - EXIT INT TERM
  for state in "$client_b_state" "$client_a_state" "$server_state"; do
    if [[ -f $state ]]; then
      log "Deleting resources recorded in $state..."
      VPN_STATE_FILE=$state "$CREATE_DROPLET" --delete || status=1
    fi
  done
  if [[ -n $shared_key_id ]]; then
    log "Deleting shared temporary DigitalOcean SSH key $shared_key_id..."
    do_delete_ssh_key "$shared_key_id" || status=1
  fi
  rm -rf -- "$work_dir"
  exit "$status"
}
trap cleanup EXIT INT TERM

log "Creating and registering a shared temporary SSH key..."
ssh-keygen -q -t ed25519 -N '' -C "VPNSample three-node E2E $run_id" -f "$shared_key"
shared_key_id=$(doctl compute ssh-key import "vpnsample-mesh-$run_id-key" \
  --public-key-file "$shared_key.pub" --format ID --no-header)
[[ $shared_key_id =~ ^[0-9]+$ ]] || fail "Could not parse the imported SSH key ID."
do_wait_for_ssh_key "$shared_key_id"
sleep 3

log "Creating VPN server in $SERVER_REGION..."
VPN_STATE_FILE=$server_state "$CREATE_DROPLET" \
  --name "vpnsample-mesh-server-$run_id" --region "$SERVER_REGION" --size "$DO_SIZE" \
  --ssh-key-id "$shared_key_id" --ssh-key "$shared_key"

log "Creating nginx client A in $CLIENT_A_REGION..."
VPN_STATE_FILE=$client_a_state "$CREATE_DROPLET" \
  --name "vpnsample-mesh-a-$run_id" --region "$CLIENT_A_REGION" --size "$DO_SIZE" \
  --ssh-key-id "$shared_key_id" --ssh-key "$shared_key"

log "Creating requester client B in $CLIENT_B_REGION..."
VPN_STATE_FILE=$client_b_state "$CREATE_DROPLET" \
  --name "vpnsample-mesh-b-$run_id" --region "$CLIENT_B_REGION" --size "$DO_SIZE" \
  --ssh-key-id "$shared_key_id" --ssh-key "$shared_key"

server_ip=$(state_value "$server_state" DROPLET_IP)
server_key=$(state_value "$server_state" SSH_KEY_PATH)
client_a_ip=$(state_value "$client_a_state" DROPLET_IP)
client_a_key=$(state_value "$client_a_state" SSH_KEY_PATH)
client_b_ip=$(state_value "$client_b_state" DROPLET_IP)
client_b_key=$(state_value "$client_b_state" SSH_KEY_PATH)

log "Waiting for cloud-init on VPN server $server_ip..."
wait_for_ssh "$server_ip" "$server_key" root 36 5 "$work_dir/known_hosts"
ssh_options_for "$server_key" "$work_dir/known_hosts"
ssh "${SSH_OPTIONS[@]}" "root@$server_ip" 'cloud-init status --wait >/dev/null'

log "Deploying VPN server with profile '$VPN_PROFILE'..."
VPN_STATE_FILE=$server_state VPN_PROFILE=$VPN_PROFILE \
  SSH_KNOWN_HOSTS_FILE=$work_dir/known_hosts "$DEPLOY_SERVER"
vpn_port=$(state_value "$server_state" VPN_PORT)
tls_server_name=$(state_value "$server_state" VPN_TLS_SERVER_NAME)
tls_pinned_certificate=$(state_value "$server_state" VPN_TLS_PINNED_CERTIFICATE)
cover_token=$(state_value "$server_state" VPN_COVER_TOKEN)
[[ -f $tls_pinned_certificate ]] \
  || fail "Pinned TLS certificate was not created: $tls_pinned_certificate"
validate_cover_token "$cover_token"

log "Publishing current VPN client..."
dotnet publish "$PROJECT_ROOT/Client/Client.csproj" -c Release --no-self-contained \
  -o "$publish_dir" >/dev/null
read -r dns_server_ipv4 dns_server_ipv6 \
  < <(dotnet "$publish_dir/Client.dll" --print-server-addresses)
[[ -n $dns_server_ipv4 && -n $dns_server_ipv6 ]] \
  || fail "Client did not report the overlay DNS server addresses."

log "Preparing client A with nginx..."
wait_for_ssh "$client_a_ip" "$client_a_key" root 36 5 "$work_dir/known_hosts"
install_client "$client_a_ip" "$client_a_key" true

log "Preparing client B..."
wait_for_ssh "$client_b_ip" "$client_b_key" root 36 5 "$work_dir/known_hosts"
install_client "$client_b_ip" "$client_b_key" false

log "Connecting client A first..."
start_vpn_client "$client_a_ip" "$client_a_key" "$server_ip" "$CLIENT_A_NAME"
wait_for_tunnel "$client_a_ip" "$client_a_key"
configure_overlay_dns "$client_a_ip" "$client_a_key"

log "Connecting client B second..."
start_vpn_client "$client_b_ip" "$client_b_key" "$server_ip" "$CLIENT_B_NAME"
wait_for_tunnel "$client_b_ip" "$client_b_key"
configure_overlay_dns "$client_b_ip" "$client_b_key"

verify_three_node_topology
