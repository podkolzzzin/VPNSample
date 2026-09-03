#!/usr/bin/env bash
set -Eeuo pipefail

PROJECT_ROOT=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)
SCRIPT_DIR=$PROJECT_ROOT/scripts
VPNSAMPLE_LOG_PREFIX=e2e-vpn-iptest
# shellcheck source=scripts/lib/common.sh
source "$SCRIPT_DIR/lib/common.sh"
# shellcheck source=scripts/lib/ssh.sh
source "$SCRIPT_DIR/lib/ssh.sh"

CREATE_DROPLET=$SCRIPT_DIR/create-droplet.sh
DEPLOY_SERVER=$SCRIPT_DIR/deploy-server.sh
REMOTE_CLIENT_SETUP=$SCRIPT_DIR/remote/configure-client.sh
REMOTE_TEST=$SCRIPT_DIR/remote/test-exit-ip.sh
SERVER_STATE=${VPN_SERVER_STATE:-$PROJECT_ROOT/.vpn-droplet.env}
CLIENT_PROJECT=$PROJECT_ROOT/Client/Client.csproj
DO_REGION=${DO_REGION:-ams3}
DO_SIZE=${DO_SIZE:-s-1vcpu-1gb}
DO_IMAGE=${DO_IMAGE:-ubuntu-24-04-x64}
PROBE_NAME=${PROBE_NAME:-vpnsample-iptest-$(date -u +%Y%m%d-%H%M%S)}

usage() {
  cat <<'EOF'
Usage: ./e2e_vpn_iptest.sh

Redeploy the server recorded in .vpn-droplet.env, create one disposable probe
droplet, and verify its public IPv4 and IPv6 while the full tunnel is active.

Environment: VPN_SERVER_STATE, DO_REGION, DO_SIZE, DO_IMAGE, and PROBE_NAME.
EOF
}

if (($#)); then
  case $1 in
    -h|--help) usage; exit 0 ;;
    *) fail "Unknown option: $1 (use --help)" ;;
  esac
fi

need_all doctl dotnet scp ssh ssh-keygen
for file in \
  "$CREATE_DROPLET" "$DEPLOY_SERVER" "$REMOTE_CLIENT_SETUP" "$REMOTE_TEST" \
  "$SCRIPT_DIR/lib/common.sh" "$SCRIPT_DIR/lib/routes.sh" \
  "$SERVER_STATE" "$CLIENT_PROJECT"; do
  [[ -e $file ]] || fail "Required project file not found: $file"
done
doctl account get >/dev/null

load_state "$SERVER_STATE"
SERVER_IPV4=${DROPLET_IP:?DROPLET_IP is missing from $SERVER_STATE}
SERVER_IPV6=${DROPLET_IPV6:?DROPLET_IPV6 is missing from $SERVER_STATE}

work_dir=$(mktemp -d "${TMPDIR:-/tmp}/vpnsample-iptest.XXXXXXXX")
probe_state=$work_dir/probe.env
probe_key=$work_dir/probe-key
publish_dir=$work_dir/client
probe_created=false

cleanup() {
  local status=$?
  trap - EXIT INT TERM
  if [[ $probe_created == true && -f $probe_state ]]; then
    log "Deleting the temporary probe droplet and its SSH key..."
    VPN_STATE_FILE=$probe_state "$CREATE_DROPLET" --delete || status=1
  fi
  rm -rf -- "$work_dir"
  exit "$status"
}
trap cleanup EXIT INT TERM

log "Deploying the current VPN server build to $SERVER_IPV4..."
VPN_STATE_FILE=$SERVER_STATE "$DEPLOY_SERVER"
load_state "$SERVER_STATE"
SERVER_PORT=${VPN_PORT:-4433}

log "Publishing the VPN client..."
dotnet publish "$CLIENT_PROJECT" -c Release --no-self-contained -o "$publish_dir"

log "Creating fresh DigitalOcean probe droplet $PROBE_NAME..."
VPN_STATE_FILE=$probe_state VPN_SSH_KEY_PATH=$probe_key \
  "$CREATE_DROPLET" --name "$PROBE_NAME" --region "$DO_REGION" \
  --size "$DO_SIZE" --image "$DO_IMAGE"
probe_created=true

PROBE_IPV4=$(state_value "$probe_state" DROPLET_IP)
PROBE_SSH_KEY=$(state_value "$probe_state" SSH_KEY_PATH)
remote=root@$PROBE_IPV4
remote_dir=/opt/vpnsample-iptest

log "Waiting for SSH on probe droplet $PROBE_IPV4..."
wait_for_ssh "$PROBE_IPV4" "$PROBE_SSH_KEY"

log "Installing probe prerequisites and .NET 10 runtime..."
ssh "${SSH_OPTIONS[@]}" "$remote" bash -s -- "$remote_dir" false true \
  <"$REMOTE_CLIENT_SETUP"

log "Uploading the client and detached IP-test runner..."
ssh "${SSH_OPTIONS[@]}" "$remote" "mkdir -p '$remote_dir/lib'"
scp "${SSH_OPTIONS[@]}" -r "$publish_dir/." "$remote:$remote_dir/app/"
scp "${SSH_OPTIONS[@]}" "$REMOTE_TEST" "$remote:$remote_dir/test-exit-ip.sh"
scp "${SSH_OPTIONS[@]}" "$SCRIPT_DIR/lib/common.sh" "$SCRIPT_DIR/lib/routes.sh" \
  "$remote:$remote_dir/lib/"

remote_status=$remote_dir/result.status
remote_log=$remote_dir/result.log
remote_dotnet=$remote_dir/dotnet/dotnet
remote_client=$remote_dir/app/Client.dll
ssh "${SSH_OPTIONS[@]}" "$remote" \
  "chmod +x '$remote_dir/test-exit-ip.sh'; rm -f '$remote_status' '$remote_log'; nohup '$remote_dir/test-exit-ip.sh' '$remote_status' '$SERVER_IPV4' '$SERVER_IPV6' '$SERVER_PORT' '$remote_dotnet' '$remote_client' >'$remote_log' 2>&1 </dev/null &"

log "The detached test may interrupt SSH while replacing its default route."
test_finished=false
for attempt in $(seq 1 180); do
  if ssh "${SSH_OPTIONS[@]}" "$remote" "test -f '$remote_status'" 2>/dev/null; then
    test_finished=true
    break
  fi
  sleep 5
done
[[ $test_finished == true ]] || fail "Remote test did not finish within 15 minutes."

printf '\n===== REMOTE PROBE OUTPUT =====\n'
ssh "${SSH_OPTIONS[@]}" "$remote" "cat '$remote_log'"
test_status=$(ssh "${SSH_OPTIONS[@]}" "$remote" "cat '$remote_status'")
[[ $test_status == 0 ]] || fail "Remote VPN IP test failed with status $test_status."
