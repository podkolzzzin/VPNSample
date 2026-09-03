#!/usr/bin/env bash
set -Eeuo pipefail

SCRIPT_DIR=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)
LIB_DIR=$(cd -- "$SCRIPT_DIR/../lib" && pwd)
VPNSAMPLE_LOG_PREFIX=lib-tests
# shellcheck source=../lib/common.sh
source "$LIB_DIR/common.sh"
# shellcheck source=../lib/ssh.sh
source "$LIB_DIR/ssh.sh"
# shellcheck source=../lib/routes.sh
source "$LIB_DIR/routes.sh"
# shellcheck source=../lib/digitalocean.sh
source "$LIB_DIR/digitalocean.sh"

assert_equal() {
  local expected=$1
  local actual=$2
  local label=$3
  [[ $actual == "$expected" ]] \
    || fail "$label: expected '$expected', got '$actual'."
}

state_file=$(mktemp)
trap 'rm -f -- "$state_file"' EXIT
printf 'SAMPLE_VALUE=%q\n' 'value with spaces' >"$state_file"
assert_equal 'value with spaces' "$(state_value "$state_file" SAMPLE_VALUE)" \
  'state_value'

ssh_options_for /tmp/test-key /tmp/test-known-hosts
assert_equal /tmp/test-key "${SSH_OPTIONS[1]}" 'SSH identity path'
[[ " ${SSH_OPTIONS[*]} " == *" UserKnownHostsFile=/tmp/test-known-hosts "* ]] \
  || fail 'SSH known-hosts option is missing.'

ip() {
  case "$*" in
    '-4 route show default')
      printf '%s\n' 'default via 192.0.2.1 dev eth0'
      ;;
    '-4 route get 203.0.113.10')
      printf '%s\n' '203.0.113.10 via 192.0.2.1 dev eth0 src 192.0.2.2'
      ;;
    '-4 route show 203.0.113.10/32')
      ;;
    '-4 route get 1.1.1.1'|'-6 route get 2606:4700:4700::1111')
      printf '%s\n' 'test dev svpn0 src test'
      ;;
    *)
      ;;
  esac
}

vpn_routes_capture 203.0.113.10
assert_equal 'default via 192.0.2.1 dev eth0' "$VPN_ORIGINAL_DEFAULT" \
  'captured default route'
assert_equal 192.0.2.1 "$VPN_SERVER_GATEWAY" 'captured server gateway'
assert_equal eth0 "$VPN_SERVER_INTERFACE" 'captured server interface'
vpn_routes_apply 203.0.113.10 50
assert_equal svpn0 "$(route_interface 4 1.1.1.1)" 'IPv4 route selection'
assert_equal svpn0 "$(route_interface 6 2606:4700:4700::1111)" \
  'IPv6 route selection'
vpn_routes_restore 203.0.113.10 50

droplet_gets=0
sleep() { :; }
doctl() {
  if [[ $* == 'compute droplet get 42' ]]; then
    ((droplet_gets += 1))
    ((droplet_gets == 1)) && return 0
    return 1
  fi
  return 0
}
do_delete_droplet 42 test-droplet >/dev/null
assert_equal 2 "$droplet_gets" 'droplet deletion polling'

printf '[lib-tests] Shared shell modules passed.\n'
