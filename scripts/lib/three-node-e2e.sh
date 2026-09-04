#!/usr/bin/env bash

# Operations performed against one of the two E2E client droplets.

install_client() {
  local ip=$1
  local key=$2
  local install_nginx=$3
  ssh_options_for "$key" "$work_dir/known_hosts"
  ssh "${SSH_OPTIONS[@]}" "root@$ip" bash -s -- \
    /opt/vpnsample-client "$install_nginx" false <"$REMOTE_CLIENT_SETUP"
  scp "${SSH_OPTIONS[@]}" -r "$publish_dir/." \
    "root@$ip:/opt/vpnsample-client/app/"
  scp "${SSH_OPTIONS[@]}" "$tls_pinned_certificate" \
    "root@$ip:/opt/vpnsample-client/tls.crt"
}

start_vpn_client() {
  local ip=$1
  local key=$2
  local server_ip=$3
  local node_name=$4
  ssh_options_for "$key" "$work_dir/known_hosts"
  ssh "${SSH_OPTIONS[@]}" "root@$ip" \
    "nohup env VPN_PROFILE='$VPN_PROFILE' VPN_COVER_TOKEN='$cover_token' VPN_TLS_SERVER_NAME='$tls_server_name' VPN_TLS_PINNED_CERTIFICATE=/opt/vpnsample-client/tls.crt /opt/vpnsample-client/dotnet/dotnet /opt/vpnsample-client/app/Client.dll '$server_ip' '$vpn_port' '$node_name' >/opt/vpnsample-client/client.log 2>&1 </dev/null & echo \$! >/opt/vpnsample-client/client.pid"
}

configure_overlay_dns() {
  local ip=$1
  local key=$2
  ssh_options_for "$key" "$work_dir/known_hosts"
  ssh "${SSH_OPTIONS[@]}" "root@$ip" \
    "resolvectl dns svpn0 '$dns_server_ipv4'; resolvectl domain svpn0 '$VPN_DNS_ZONE'; resolvectl default-route svpn0 false; resolvectl flush-caches"
}

wait_for_tunnel() {
  local ip=$1
  local key=$2
  ssh_options_for "$key" "$work_dir/known_hosts"
  for attempt in $(seq 1 30); do
    if ssh "${SSH_OPTIONS[@]}" "root@$ip" \
      'test -e /sys/class/net/svpn0' 2>/dev/null; then
      sleep 2
      if ssh "${SSH_OPTIONS[@]}" "root@$ip" \
        'test -e /sys/class/net/svpn0 && kill -0 "$(cat /opt/vpnsample-client/client.pid)"' \
        2>/dev/null; then
        return
      fi
      break
    fi
    if ! ssh "${SSH_OPTIONS[@]}" "root@$ip" \
      'test -s /opt/vpnsample-client/client.pid && kill -0 "$(cat /opt/vpnsample-client/client.pid)"' \
      2>/dev/null; then
      ssh "${SSH_OPTIONS[@]}" "root@$ip" \
        'cat /opt/vpnsample-client/client.log' || true
      fail "VPN client exited before creating svpn0 on $ip."
    fi
    sleep 1
  done
  ssh "${SSH_OPTIONS[@]}" "root@$ip" \
    'cat /opt/vpnsample-client/client.log' || true
  fail "VPN client did not remain connected on $ip."
}

check_websocket_transport() {
  local ip=$1
  local key=$2
  local expected="WebSocket transport: wss://$tls_server_name:$vpn_port/api/v1/events"
  ssh_options_for "$key" "$work_dir/known_hosts"
  if ! ssh "${SSH_OPTIONS[@]}" "root@$ip" \
    "grep -Fqx '$expected' /opt/vpnsample-client/client.log"; then
    ssh "${SSH_OPTIONS[@]}" "root@$ip" \
      'cat /opt/vpnsample-client/client.log' || true
    fail "Client on $ip did not confirm the expected WebSocket transport."
  fi
}

tunnel_address() {
  local family=$1
  local ip=$2
  local key=$3
  local scope=
  [[ $family == 6 ]] && scope='scope global'
  ssh_options_for "$key" "$work_dir/known_hosts"
  ssh "${SSH_OPTIONS[@]}" "root@$ip" \
    "ip -o -'$family' address show dev svpn0 $scope | awk '{ split(\$4, a, \"/\"); print a[1]; exit }'"
}

check_exit_node() {
  local ip=$1
  local key=$2
  ssh_options_for "$key" "$work_dir/known_hosts"
  ssh "${SSH_OPTIONS[@]}" "root@$ip" \
    'ip route replace 1.1.1.1/32 dev svpn0; ip -6 route replace 2606:4700:4700::1111/128 dev svpn0; ping -c 2 -W 5 1.1.1.1; ping -6 -c 2 -W 5 2606:4700:4700::1111'
}

assert_overlay_route() {
  local ip=$1
  local key=$2
  local family=$3
  local destination=$4
  local selected_route
  ssh_options_for "$key" "$work_dir/known_hosts"
  selected_route=$(ssh "${SSH_OPTIONS[@]}" "root@$ip" \
    "ip -'$family' route get '$destination'")
  [[ " $selected_route " == *" dev svpn0 "* ]] \
    || fail "An automatically created overlay route did not select svpn0: $selected_route"
}

ping_peer() {
  local ip=$1
  local key=$2
  local family=$3
  local destination=$4
  ssh_options_for "$key" "$work_dir/known_hosts"
  ssh "${SSH_OPTIONS[@]}" "root@$ip" \
    "ping ${family:+-$family} -c 2 -W 3 '$destination'"
}

print_network_diagnostics() {
  printf '\n===== SERVER NETWORK DIAGNOSTICS =====\n'
  ssh_options_for "$server_key" "$work_dir/known_hosts"
  ssh "${SSH_OPTIONS[@]}" "root@$server_ip" \
    "sysctl net.ipv4.ip_forward net.ipv6.conf.all.forwarding; ip -4 route; ip -6 route; iptables -S FORWARD; ip6tables -S FORWARD; ip -br address show | grep -E 'svpn|eth0'"
  printf '\n===== CLIENT A NETWORK DIAGNOSTICS =====\n'
  ssh_options_for "$client_a_key" "$work_dir/known_hosts"
  ssh "${SSH_OPTIONS[@]}" "root@$client_a_ip" \
    "cat /opt/vpnsample-client/client.log; ip -4 route get '$client_b_tunnel_v4'; ip -6 route get '$client_b_tunnel_v6'; ip -s link show svpn0"
  printf '\n===== CLIENT B NETWORK DIAGNOSTICS =====\n'
  ssh_options_for "$client_b_key" "$work_dir/known_hosts"
  ssh "${SSH_OPTIONS[@]}" "root@$client_b_ip" \
    "cat /opt/vpnsample-client/client.log; ip -4 route get '$client_a_tunnel_v4'; ip -6 route get '$client_a_tunnel_v6'; ip -s link show svpn0"
}
