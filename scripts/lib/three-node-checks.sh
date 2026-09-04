#!/usr/bin/env bash

# Assertions run after both E2E clients have established their tunnels.

verify_three_node_topology() {
  log "Confirming TLS and standard WebSocket transport on both clients..."
  check_websocket_transport "$client_a_ip" "$client_a_key"
  check_websocket_transport "$client_b_ip" "$client_b_key"

  client_a_tunnel_v4=$(tunnel_address 4 "$client_a_ip" "$client_a_key")
  client_a_tunnel_v6=$(tunnel_address 6 "$client_a_ip" "$client_a_key")
  client_b_tunnel_v4=$(tunnel_address 4 "$client_b_ip" "$client_b_key")
  client_b_tunnel_v6=$(tunnel_address 6 "$client_b_ip" "$client_b_key")
  assert_distinct_tunnel_addresses

  log "A overlay: $client_a_tunnel_v4, $client_a_tunnel_v6"
  log "B overlay: $client_b_tunnel_v4, $client_b_tunnel_v6"
  assert_single_server_tun
  assert_overlay_route "$client_a_ip" "$client_a_key" 4 "$client_b_tunnel_v4"
  assert_overlay_route "$client_a_ip" "$client_a_key" 6 "$client_b_tunnel_v6"
  assert_overlay_route "$client_b_ip" "$client_b_key" 4 "$client_a_tunnel_v4"
  assert_overlay_route "$client_b_ip" "$client_b_key" 6 "$client_a_tunnel_v6"

  check_peer_reachability

  log "Checking IPv4 and IPv6 exit-node forwarding from both clients..."
  check_exit_node "$client_a_ip" "$client_a_key"
  check_exit_node "$client_b_ip" "$client_b_key"

  check_nginx_reachability
  print_three_node_result
}

assert_distinct_tunnel_addresses() {
  if [[ -z $client_a_tunnel_v4 || -z $client_a_tunnel_v6 \
    || -z $client_b_tunnel_v4 || -z $client_b_tunnel_v6 ]]; then
    ssh_options_for "$client_a_key" "$work_dir/known_hosts"
    ssh "${SSH_OPTIONS[@]}" "root@$client_a_ip" \
      'cat /opt/vpnsample-client/client.log' || true
    ssh_options_for "$client_b_key" "$work_dir/known_hosts"
    ssh "${SSH_OPTIONS[@]}" "root@$client_b_ip" \
      'cat /opt/vpnsample-client/client.log' || true
    ssh_options_for "$server_key" "$work_dir/known_hosts"
    ssh "${SSH_OPTIONS[@]}" "root@$server_ip" \
      'journalctl -u vpnsample.service --no-pager -n 100' || true
    fail "At least one client did not receive both tunnel addresses."
  fi
  [[ $client_a_tunnel_v4 != "$client_b_tunnel_v4" ]] \
    || fail "Clients received the same IPv4 address."
  [[ $client_a_tunnel_v6 != "$client_b_tunnel_v6" ]] \
    || fail "Clients received the same IPv6 address."
}

assert_single_server_tun() {
  local count
  ssh_options_for "$server_key" "$work_dir/known_hosts"
  count=$(ssh "${SSH_OPTIONS[@]}" "root@$server_ip" \
    "find /sys/class/net -maxdepth 1 -name 'svpn*' | wc -l")
  [[ $count == 1 ]] || fail "Expected exactly one server TUN, found $count."
}

check_peer_reachability() {
  log "Checking bidirectional IPv4 and IPv6 reachability..."
  set +e
  ping_peer "$client_a_ip" "$client_a_key" 4 "$client_b_tunnel_v4"; a_to_b_v4=$?
  ping_peer "$client_a_ip" "$client_a_key" 6 "$client_b_tunnel_v6"; a_to_b_v6=$?
  ping_peer "$client_b_ip" "$client_b_key" 4 "$client_a_tunnel_v4"; b_to_a_v4=$?
  ping_peer "$client_b_ip" "$client_b_key" 6 "$client_a_tunnel_v6"; b_to_a_v6=$?
  set -e

  if ((a_to_b_v4 || a_to_b_v6 || b_to_a_v4 || b_to_a_v6)); then
    print_network_diagnostics
    fail "Peer reachability failed: A->B v4=$a_to_b_v4 v6=$a_to_b_v6, B->A v4=$b_to_a_v4 v6=$b_to_a_v6."
  fi
}

check_nginx_reachability() {
  log "Requesting nginx on client A from client B through the VPN..."
  ssh_options_for "$client_b_key" "$work_dir/known_hosts"
  http_v4=$(ssh "${SSH_OPTIONS[@]}" "root@$client_b_ip" \
    "curl --noproxy '*' --fail --silent --show-error --connect-timeout 5 --max-time 15 'http://$client_a_tunnel_v4/'")
  http_v6=$(ssh "${SSH_OPTIONS[@]}" "root@$client_b_ip" \
    "curl --noproxy '*' --fail --silent --show-error --connect-timeout 5 --max-time 15 'http://[$client_a_tunnel_v6]/'")
  [[ $http_v4 == vpn-mesh-nginx-ok ]] \
    || fail "Unexpected nginx response over IPv4: $http_v4"
  [[ $http_v6 == vpn-mesh-nginx-ok ]] \
    || fail "Unexpected nginx response over IPv6: $http_v6"

  ssh_options_for "$client_a_key" "$work_dir/known_hosts"
  nginx_log=$(ssh "${SSH_OPTIONS[@]}" "root@$client_a_ip" \
    'tail -n 10 /var/log/nginx/access.log')
  grep -Fq "$client_b_tunnel_v4" <<<"$nginx_log" \
    || fail "nginx did not record client B's overlay IPv4 address."
  grep -Fq "$client_b_tunnel_v6" <<<"$nginx_log" \
    || fail "nginx did not record client B's overlay IPv6 address."
}

print_three_node_result() {
  printf '\n===== THREE-NODE RESULT =====\n'
  printf 'PASS: the server used one shared TUN and clients selected automatic overlay routes.\n'
  printf 'PASS: clients reached each other through the VPN over IPv4 and IPv6.\n'
  printf 'PASS: both clients reached the internet through the exit node over IPv4 and IPv6.\n'
  printf 'PASS: client B fetched nginx from client A over IPv4 and IPv6.\n'
  printf 'Server region/IP: %s / %s\n' "$SERVER_REGION" "$server_ip"
  printf 'Client A region/overlay: %s / %s / %s\n' \
    "$CLIENT_A_REGION" "$client_a_tunnel_v4" "$client_a_tunnel_v6"
  printf 'Client B region/overlay: %s / %s / %s\n' \
    "$CLIENT_B_REGION" "$client_b_tunnel_v4" "$client_b_tunnel_v6"
  printf 'nginx response: %s\n' "$http_v4"
}
