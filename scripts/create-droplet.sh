#!/usr/bin/env bash
set -Eeuo pipefail

SCRIPT_DIR=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)
PROJECT_ROOT=$(cd -- "$SCRIPT_DIR/.." && pwd)
VPNSAMPLE_LOG_PREFIX=create-droplet
# shellcheck source=lib/common.sh
source "$SCRIPT_DIR/lib/common.sh"
# shellcheck source=lib/digitalocean.sh
source "$SCRIPT_DIR/lib/digitalocean.sh"
STATE_FILE=${VPN_STATE_FILE:-$PROJECT_ROOT/.vpn-droplet.env}

name=${DROPLET_NAME:-vpnsample-$(date -u +%Y%m%d-%H%M%S)}
region=${DO_REGION:-ams3}
size=${DO_SIZE:-s-1vcpu-1gb}
image=${DO_IMAGE:-ubuntu-24-04-x64}
ssh_key_id=${DO_SSH_KEY_ID:-}
ssh_key_path=${SSH_KEY_PATH:-}
managed_key_path=${VPN_SSH_KEY_PATH:-$PROJECT_ROOT/.vpn-ssh-key}
managed_ssh_key=false
ssh_key_name=
delete=false

usage() {
  cat <<'EOF'
Usage:
  scripts/create-droplet.sh [options]
  scripts/create-droplet.sh --delete

Create a temporary DigitalOcean droplet and save its connection details in
.vpn-droplet.env. --delete removes exactly the droplet recorded in that file.

Options:
  --name NAME          Droplet name (default: vpnsample-<UTC timestamp>)
  --region REGION      DigitalOcean region (default: ams3)
  --size SIZE          Droplet size (default: s-1vcpu-1gb)
  --image IMAGE        Image slug (default: ubuntu-24-04-x64)
  --ssh-key-id ID      Use an existing DigitalOcean SSH key instead
  --ssh-key PATH       Matching existing local private key path
  --state-file PATH    State file location
  --delete             Delete the droplet recorded in the state file
  -h, --help           Show this help

By default, the script creates a dedicated passwordless Ed25519 key, registers
its public key in DigitalOcean, and removes both copies with --delete. To use an
existing key instead, pass both --ssh-key-id and --ssh-key.

Environment equivalents: DROPLET_NAME, DO_REGION, DO_SIZE, DO_IMAGE,
DO_SSH_KEY_ID, SSH_KEY_PATH, VPN_SSH_KEY_PATH, and VPN_STATE_FILE.
EOF
}

while (($#)); do
  case $1 in
    --name) name=${2:?Missing value for --name}; shift 2 ;;
    --region) region=${2:?Missing value for --region}; shift 2 ;;
    --size) size=${2:?Missing value for --size}; shift 2 ;;
    --image) image=${2:?Missing value for --image}; shift 2 ;;
    --ssh-key-id) ssh_key_id=${2:?Missing value for --ssh-key-id}; shift 2 ;;
    --ssh-key) ssh_key_path=${2:?Missing value for --ssh-key}; shift 2 ;;
    --state-file) STATE_FILE=${2:?Missing value for --state-file}; shift 2 ;;
    --delete) delete=true; shift ;;
    -h|--help) usage; exit 0 ;;
    *) fail "Unknown option: $1 (use --help)" ;;
  esac
done

need doctl

if [[ $delete == true ]]; then
  doctl account get >/dev/null
  load_state "$STATE_FILE"
  [[ -n ${DROPLET_ID:-} ]] || fail "DROPLET_ID is missing from $STATE_FILE"
  MANAGED_SSH_KEY=${MANAGED_SSH_KEY:-false}
  MANAGED_TLS_CERTIFICATE=${MANAGED_TLS_CERTIFICATE:-false}

  do_delete_droplet "$DROPLET_ID" "${DROPLET_NAME:-unknown}" \
    || fail "Droplet deletion was not confirmed; keeping $STATE_FILE for recovery."

  if [[ $MANAGED_SSH_KEY == true ]]; then
    [[ -n ${DO_SSH_KEY_ID:-} ]] || fail "DO_SSH_KEY_ID is missing from $STATE_FILE"
    [[ -n ${SSH_KEY_PATH:-} ]] || fail "SSH_KEY_PATH is missing from $STATE_FILE"
    do_delete_ssh_key "$DO_SSH_KEY_ID" \
      || fail "SSH key deletion was not confirmed; keeping local key and $STATE_FILE."
    rm -f -- "$SSH_KEY_PATH" "${SSH_KEY_PATH}.pub"
    log "Removed local temporary SSH key files."
  fi

  if [[ $MANAGED_TLS_CERTIFICATE == true && -n ${VPN_TLS_PINNED_CERTIFICATE:-} ]]; then
    rm -f -- "$VPN_TLS_PINNED_CERTIFICATE"
    log "Removed local temporary TLS certificate."
  fi

  rm -f -- "$STATE_FILE"
  log "Cleanup complete; removed $STATE_FILE"
  exit 0
fi

[[ ! -e $STATE_FILE ]] || fail "$STATE_FILE already exists. Delete its droplet with '$0 --delete' first."
doctl account get >/dev/null

created_id=
cleanup_failed_create() {
  if [[ -n $created_id ]]; then
    log "Creation did not complete; deleting droplet ID $created_id..."
    doctl compute droplet delete "$created_id" --force >/dev/null 2>&1 || true
  fi
  if [[ $managed_ssh_key == true ]]; then
    if [[ -n $ssh_key_id ]]; then
      log "Creation did not complete; removing DigitalOcean SSH key $ssh_key_id..."
      doctl compute ssh-key delete "$ssh_key_id" --force >/dev/null 2>&1 || true
    fi
    rm -f -- "$ssh_key_path" "${ssh_key_path}.pub"
  fi
}
trap cleanup_failed_create EXIT

fingerprint_for_key() {
  ssh-keygen -E md5 -lf "$1" | awk '{ sub(/^MD5:/, "", $2); print $2 }'
}

if [[ -n $ssh_key_id || -n $ssh_key_path ]]; then
  [[ -n $ssh_key_id && -n $ssh_key_path ]] \
    || fail "Pass both --ssh-key-id and --ssh-key when using an existing key."
  [[ -f $ssh_key_path ]] || fail "Private SSH key not found: $ssh_key_path"
  [[ -f ${ssh_key_path}.pub ]] || fail "Public SSH key not found: ${ssh_key_path}.pub"
  local_fingerprint=$(fingerprint_for_key "${ssh_key_path}.pub")
  cloud_fingerprint=$(doctl compute ssh-key get "$ssh_key_id" \
    --format FingerPrint --no-header)
  [[ $cloud_fingerprint == "$local_fingerprint" ]] \
    || fail "SSH key ID $ssh_key_id does not match ${ssh_key_path}.pub."
else
  need ssh-keygen
  ssh_key_path=$managed_key_path
  ssh_key_name="${name}-key"
  [[ ! -e $ssh_key_path && ! -e ${ssh_key_path}.pub ]] \
    || fail "Temporary SSH key path already exists: $ssh_key_path"
  log "Generating dedicated passwordless Ed25519 key at $ssh_key_path..."
  umask 077
  ssh-keygen -q -t ed25519 -N '' -C "VPNSample temporary key for $name" \
    -f "$ssh_key_path"
  managed_ssh_key=true
  log "Registering temporary public key in DigitalOcean..."
  ssh_key_id=$(doctl compute ssh-key import "$ssh_key_name" \
    --public-key-file "${ssh_key_path}.pub" \
    --format ID \
    --no-header)
  [[ $ssh_key_id =~ ^[0-9]+$ ]] || fail "Could not parse the imported SSH key ID."
fi

log "Creating '$name' in $region ($size, $image) with SSH key $ssh_key_id..."
created_id=$(doctl compute droplet create "$name" \
  --region "$region" \
  --image "$image" \
  --size "$size" \
  --ssh-keys "$ssh_key_id" \
  --enable-ipv6 \
  --tag-name vpnsample-temporary \
  --format ID \
  --no-header)
[[ $created_id =~ ^[0-9]+$ ]] || fail "Could not parse the droplet ID from doctl output."

log "Droplet ID $created_id created; waiting for it to become active..."
do_wait_for_droplet_active "$created_id"
droplet_ip=$(doctl compute droplet get "$created_id" --format PublicIPv4 --no-header)
[[ -n $droplet_ip ]] || fail "Droplet became active but has no public IPv4 address."
droplet_ipv6=$(doctl compute droplet get "$created_id" --format PublicIPv6 --no-header)
[[ -n $droplet_ipv6 ]] || fail "Droplet became active but has no public IPv6 address."

umask 077
{
  printf 'DROPLET_ID=%q\n' "$created_id"
  printf 'DROPLET_NAME=%q\n' "$name"
  printf 'DROPLET_IP=%q\n' "$droplet_ip"
  printf 'DROPLET_IPV6=%q\n' "$droplet_ipv6"
  printf 'DROPLET_REGION=%q\n' "$region"
  printf 'DO_SSH_KEY_ID=%q\n' "$ssh_key_id"
  printf 'SSH_KEY_PATH=%q\n' "$ssh_key_path"
  printf 'MANAGED_SSH_KEY=%q\n' "$managed_ssh_key"
  printf 'DO_SSH_KEY_NAME=%q\n' "$ssh_key_name"
  printf 'VPN_PORT=%q\n' "443"
} >"$STATE_FILE"

trap - EXIT
log "Droplet is active: $droplet_ip (ID $created_id)"
log "Public IPv6: $droplet_ipv6"
log "State saved to $STATE_FILE"
log "Next: $PROJECT_ROOT/scripts/deploy-server.sh"
log "Cleanup when finished: $PROJECT_ROOT/scripts/create-droplet.sh --delete"
