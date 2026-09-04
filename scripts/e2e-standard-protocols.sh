#!/usr/bin/env bash
set -Eeuo pipefail

: "${RUVDS_IP:?RUVDS_IP is required}"
: "${RUVDS_USER:?RUVDS_USER is required}"
: "${RUVDS_PASSWORD:?RUVDS_PASSWORD is required}"
script_dir=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)
profiles=${STANDARD_VPN_OUTPUT_DIR:-$script_dir/standard-vpn-clients}
instagram_ip=${INSTAGRAM_IPV4:-$(getent ahostsv4 www.instagram.com | awk 'NR==1 {print $1}')}
: "${instagram_ip:?Could not resolve www.instagram.com before changing VPN routes}"
ovpn_profile=$profiles/openvpn-client.ovpn
wg_profile=$profiles/wireguard-client.conf
[[ -f $ovpn_profile && -f $wg_profile ]] || {
  echo "Profiles missing. Run $script_dir/setup-openvpn-wireguard.sh first." >&2
  exit 1
}

LOCAL_OVPN_TUNNEL=false
LOCAL_OVPN_DATA=false
LOCAL_WG_HANDSHAKE=false
LOCAL_WG_DATA=false
LOCAL_OVPN_INSTAGRAM=false
LOCAL_WG_INSTAGRAM=false
yes_no() { [[ $1 == true ]] && printf PASS || printf FAIL; }
verdict() { [[ $1 == true && $2 == true ]] && printf WORKS || printf BLOCKED/FAILED; }

run_local_tests() {
  command -v openvpn >/dev/null || { echo 'openvpn is not installed locally' >&2; return 1; }
  command -v wg >/dev/null || { echo 'wireguard-tools is not installed locally' >&2; return 1; }
  sudo -n true 2>/dev/null || { echo 'Passwordless sudo is unavailable locally' >&2; return 1; }
  [[ -c /dev/net/tun ]] || { echo '/dev/net/tun is unavailable locally' >&2; return 1; }
  local test_dir ovpn_pid= private_key peer_key endpoint key_file handshake
  test_dir=$(mktemp -d /tmp/standard-vpn-local.XXXXXXXX)
  cleanup_local() {
    [[ -n $ovpn_pid ]] && sudo kill "$ovpn_pid" 2>/dev/null || true
    sudo ip link del wg-e2e 2>/dev/null || true
    rm -rf -- "$test_dir"
  }
  trap cleanup_local RETURN

  sudo openvpn --config "$ovpn_profile" --route-nopull --dev ovpn-e2e --dev-type tun --daemon --writepid "$test_dir/openvpn.pid" --log "$test_dir/openvpn.log"
  for _ in $(seq 1 15); do
    if [[ -s $test_dir/openvpn.pid ]] &&
      ip -4 address show dev ovpn-e2e 2>/dev/null | grep -q '10\.90\.0\.'; then
      LOCAL_OVPN_TUNNEL=true
      break
    fi
    sleep 1
  done
  [[ -s $test_dir/openvpn.pid ]] && ovpn_pid=$(cat "$test_dir/openvpn.pid")
  ping -c 2 -W 3 -I ovpn-e2e 10.90.0.1 >/dev/null 2>&1 && LOCAL_OVPN_DATA=true
  code=$(curl --interface ovpn-e2e --resolve "www.instagram.com:443:$instagram_ip" -4LsS -o /dev/null -w '%{http_code}' --connect-timeout 15 --max-time 45 https://www.instagram.com/ || true)
  [[ $code =~ ^(200|301|302|303|307|308)$ ]] && LOCAL_OVPN_INSTAGRAM=true

  sudo ip link add wg-e2e type wireguard
  sudo ip address add 10.91.0.2/24 dev wg-e2e
  private_key=$(awk '/^PrivateKey/ {print $3}' "$wg_profile")
  peer_key=$(awk '/^PublicKey/ {print $3}' "$wg_profile")
  endpoint=$(awk '/^Endpoint/ {print $3}' "$wg_profile")
  key_file=$test_dir/wg.key
  printf '%s\n' "$private_key" >"$key_file"
  chmod 600 "$key_file"
  sudo wg set wg-e2e private-key "$key_file" peer "$peer_key" endpoint "$endpoint" allowed-ips 0.0.0.0/0 persistent-keepalive 15
  sudo ip link set wg-e2e up
  ping -c 2 -W 3 -I wg-e2e 10.91.0.1 >/dev/null 2>&1 && LOCAL_WG_DATA=true
  handshake=$(sudo wg show wg-e2e latest-handshakes | awk 'NR==1 {print $2}')
  [[ ${handshake:-0} -gt 0 ]] && LOCAL_WG_HANDSHAKE=true
  code=$(curl --interface wg-e2e --resolve "www.instagram.com:443:$instagram_ip" -4LsS -o /dev/null -w '%{http_code}' --connect-timeout 15 --max-time 45 https://www.instagram.com/ || true)
  [[ $code =~ ^(200|301|302|303|307|308)$ ]] && LOCAL_WG_INSTAGRAM=true
}

work_dir=$(mktemp -d /tmp/standard-vpn-e2e.XXXXXXXX)
askpass=$work_dir/askpass
cleanup() { rm -rf -- "$work_dir"; }
trap cleanup EXIT
printf '#!/usr/bin/env bash\nprintf '\''%%s\\n'\'' "$RUVDS_PASSWORD"\n' >"$askpass"
chmod 700 "$askpass"
export SSH_ASKPASS=$askpass SSH_ASKPASS_REQUIRE=force
ssh_opts=(-o StrictHostKeyChecking=accept-new -o "UserKnownHostsFile=$script_dir/.ruvds_known_hosts" -o PreferredAuthentications=password -o PubkeyAuthentication=no)

printf 'Testing standard VPN protocols against DigitalOcean...\n'
run_local_tests
setsid -w scp "${ssh_opts[@]}" "$ovpn_profile" "$wg_profile" "$RUVDS_USER@$RUVDS_IP:/tmp/" >/dev/null

ruvds_result=$(setsid -w ssh "${ssh_opts[@]}" "$RUVDS_USER@$RUVDS_IP" bash -s -- "$instagram_ip" <<'REMOTE'
set -Eeuo pipefail
instagram_ip=$1
restore_custom=false
if systemctl is-active --quiet vpnsample-client.service; then
  restore_custom=true
  systemctl stop vpnsample-client.service
fi
cleanup_remote() {
  [[ -s /tmp/openvpn-e2e.pid ]] && kill "$(cat /tmp/openvpn-e2e.pid)" 2>/dev/null || true
  ip link del wg-e2e 2>/dev/null || true
  rm -f /tmp/openvpn-e2e.pid /tmp/openvpn-e2e.log /tmp/openvpn-client.ovpn /tmp/wireguard-client.conf /tmp/wg-e2e.key
  [[ $restore_custom == true ]] && systemctl start vpnsample-client.service || true
}
trap cleanup_remote EXIT

ovpn_tunnel=false; ovpn_data=false; ovpn_instagram=false
openvpn --config /tmp/openvpn-client.ovpn --route-nopull --dev ovpn-e2e --dev-type tun --daemon --writepid /tmp/openvpn-e2e.pid --log /tmp/openvpn-e2e.log
for _ in $(seq 1 15); do
  if ip -4 address show dev ovpn-e2e 2>/dev/null | grep -q '10\.90\.0\.'; then
    ovpn_tunnel=true
    break
  fi
  sleep 1
done
ping -c 2 -W 3 -I ovpn-e2e 10.90.0.1 >/dev/null 2>&1 && ovpn_data=true
code=$(curl --interface ovpn-e2e --resolve "www.instagram.com:443:$instagram_ip" -4LsS -o /dev/null -w '%{http_code}' --connect-timeout 15 --max-time 45 https://www.instagram.com/ || true)
[[ $code =~ ^(200|301|302|303|307|308)$ ]] && ovpn_instagram=true

wg_handshake=false; wg_data=false; wg_instagram=false
ip link add wg-e2e type wireguard
ip address add 10.91.0.2/24 dev wg-e2e
awk '/^PrivateKey/ {print $3}' /tmp/wireguard-client.conf >/tmp/wg-e2e.key
chmod 600 /tmp/wg-e2e.key
peer_key=$(awk '/^PublicKey/ {print $3}' /tmp/wireguard-client.conf)
endpoint=$(awk '/^Endpoint/ {print $3}' /tmp/wireguard-client.conf)
wg set wg-e2e private-key /tmp/wg-e2e.key peer "$peer_key" endpoint "$endpoint" allowed-ips 0.0.0.0/0 persistent-keepalive 15
ip link set wg-e2e up
ping -c 2 -W 3 -I wg-e2e 10.91.0.1 >/dev/null 2>&1 && wg_data=true
handshake=$(wg show wg-e2e latest-handshakes | awk 'NR==1 {print $2}')
[[ ${handshake:-0} -gt 0 ]] && wg_handshake=true
code=$(curl --interface wg-e2e --resolve "www.instagram.com:443:$instagram_ip" -4LsS -o /dev/null -w '%{http_code}' --connect-timeout 15 --max-time 45 https://www.instagram.com/ || true)
[[ $code =~ ^(200|301|302|303|307|308)$ ]] && wg_instagram=true
printf 'OVPN_TUNNEL=%s\nOVPN_DATA=%s\nOVPN_INSTAGRAM=%s\nWG_HANDSHAKE=%s\nWG_DATA=%s\nWG_INSTAGRAM=%s\n' "$ovpn_tunnel" "$ovpn_data" "$ovpn_instagram" "$wg_handshake" "$wg_data" "$wg_instagram"
REMOTE
)

RUVDS_OVPN_TUNNEL=$(awk -F= '$1=="OVPN_TUNNEL" {print $2}' <<<"$ruvds_result")
RUVDS_OVPN_DATA=$(awk -F= '$1=="OVPN_DATA" {print $2}' <<<"$ruvds_result")
RUVDS_OVPN_INSTAGRAM=$(awk -F= '$1=="OVPN_INSTAGRAM" {print $2}' <<<"$ruvds_result")
RUVDS_WG_HANDSHAKE=$(awk -F= '$1=="WG_HANDSHAKE" {print $2}' <<<"$ruvds_result")
RUVDS_WG_DATA=$(awk -F= '$1=="WG_DATA" {print $2}' <<<"$ruvds_result")
RUVDS_WG_INSTAGRAM=$(awk -F= '$1=="WG_INSTAGRAM" {print $2}' <<<"$ruvds_result")
for value in "$RUVDS_OVPN_TUNNEL" "$RUVDS_OVPN_DATA" "$RUVDS_OVPN_INSTAGRAM" "$RUVDS_WG_HANDSHAKE" "$RUVDS_WG_DATA" "$RUVDS_WG_INSTAGRAM"; do
  [[ $value == true || $value == false ]] || { echo 'Could not parse RUVDS results.' >&2; exit 1; }
done

printf '\n%-12s %-11s %-10s %-10s %-11s %-15s\n' LOCATION PROTOCOL HANDSHAKE DATA INSTAGRAM RESULT
printf '%-12s %-11s %-10s %-10s %-11s %-15s\n' ------------ ----------- ---------- ---------- ----------- ---------------
printf '%-12s %-11s %-10s %-10s %-11s %-15s\n' Workspace OpenVPN "$(yes_no "$LOCAL_OVPN_TUNNEL")" "$(yes_no "$LOCAL_OVPN_DATA")" "$(yes_no "$LOCAL_OVPN_INSTAGRAM")" "$(verdict "$LOCAL_OVPN_DATA" "$LOCAL_OVPN_INSTAGRAM")"
printf '%-12s %-11s %-10s %-10s %-11s %-15s\n' Workspace WireGuard "$(yes_no "$LOCAL_WG_HANDSHAKE")" "$(yes_no "$LOCAL_WG_DATA")" "$(yes_no "$LOCAL_WG_INSTAGRAM")" "$(verdict "$LOCAL_WG_DATA" "$LOCAL_WG_INSTAGRAM")"
printf '%-12s %-11s %-10s %-10s %-11s %-15s\n' RUVDS OpenVPN "$(yes_no "$RUVDS_OVPN_TUNNEL")" "$(yes_no "$RUVDS_OVPN_DATA")" "$(yes_no "$RUVDS_OVPN_INSTAGRAM")" "$(verdict "$RUVDS_OVPN_DATA" "$RUVDS_OVPN_INSTAGRAM")"
printf '%-12s %-11s %-10s %-10s %-11s %-15s\n' RUVDS WireGuard "$(yes_no "$RUVDS_WG_HANDSHAKE")" "$(yes_no "$RUVDS_WG_DATA")" "$(yes_no "$RUVDS_WG_INSTAGRAM")" "$(verdict "$RUVDS_WG_DATA" "$RUVDS_WG_INSTAGRAM")"
printf '\nPASS/PASS: tunnel carries data. PASS/FAIL: handshake succeeds but data is blocked/dropped.\n'
printf 'The custom RUVDS VPN was restored after the native-network tests.\n'
