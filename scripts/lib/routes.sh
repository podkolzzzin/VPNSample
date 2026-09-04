#!/usr/bin/env bash

# Captures, applies, checks, and restores the routes used by a full tunnel.

vpn_routes_capture() {
  local server_ip=$1
  local route_to_server

  VPN_ORIGINAL_DEFAULT=$(ip -4 route show default | head -n 1)
  [[ -n $VPN_ORIGINAL_DEFAULT ]] || fail "No IPv4 default route found."
  route_to_server=$(ip -4 route get "$server_ip" | head -n 1)
  VPN_ORIGINAL_SERVER_ROUTE=$(ip -4 route show "$server_ip/32" | head -n 1 || true)
  VPN_SERVER_GATEWAY=$(awk \
    '{ for (i = 1; i <= NF; i++) if ($i == "via") { print $(i + 1); exit } }' \
    <<<"$route_to_server")
  VPN_SERVER_INTERFACE=$(awk \
    '{ for (i = 1; i <= NF; i++) if ($i == "dev") { print $(i + 1); exit } }' \
    <<<"$route_to_server")
  [[ -n $VPN_SERVER_INTERFACE ]] \
    || fail "Could not determine the interface used to reach $server_ip."
}

vpn_routes_apply() {
  local server_ip=$1
  local metric=$2
  local mesh_mark=${3:-}
  local mesh_table=${4:-}
  local mesh_priority=${5:-}
  if [[ -n $VPN_SERVER_GATEWAY ]]; then
    ip route replace "$server_ip/32" via "$VPN_SERVER_GATEWAY" dev "$VPN_SERVER_INTERFACE"
  else
    ip route replace "$server_ip/32" dev "$VPN_SERVER_INTERFACE"
  fi
  if [[ -n $mesh_mark ]]; then
    ip -4 route replace table "$mesh_table" default dev svpn0
    ip -4 rule add priority "$mesh_priority" \
      not fwmark "$mesh_mark" lookup "$mesh_table"
    ip -4 rule add priority "$((mesh_priority + 1))" \
      table main suppress_prefixlength 0
  else
    ip route replace default dev svpn0
  fi
  ip -6 route replace default dev svpn0 metric "$metric"
}

vpn_routes_restore() {
  local server_ip=$1
  local metric=$2
  local mesh_mark=${3:-}
  local mesh_table=${4:-}
  local mesh_priority=${5:-}
  local status=0

  # Route strings originate from iproute2 and intentionally undergo word splitting.
  # shellcheck disable=SC2086
  ip route replace $VPN_ORIGINAL_DEFAULT || status=1
  ip -6 route del default dev svpn0 metric "$metric" 2>/dev/null || true
  if [[ -n $mesh_mark ]]; then
    ip -4 rule del priority "$mesh_priority" \
      not fwmark "$mesh_mark" lookup "$mesh_table" \
      2>/dev/null || true
    ip -4 rule del priority "$((mesh_priority + 1))" \
      table main suppress_prefixlength 0 \
      2>/dev/null || true
    ip -4 route flush table "$mesh_table" 2>/dev/null || true
  fi
  ip route del "$server_ip/32" 2>/dev/null || true
  if [[ -n $VPN_ORIGINAL_SERVER_ROUTE ]]; then
    # shellcheck disable=SC2086
    ip route replace $VPN_ORIGINAL_SERVER_ROUTE || status=1
  fi
  return "$status"
}

route_interface() {
  local family=$1
  local address=$2
  ip "-$family" route get "$address" \
    | awk '{ for (i = 1; i <= NF; i++) if ($i == "dev") { print $(i + 1); exit } }'
}

marked_route_interface() {
  local address=$1
  local mark=$2
  ip -4 route get "$address" mark "$mark" \
    | awk '{ for (i = 1; i <= NF; i++) if ($i == "dev") { print $(i + 1); exit } }'
}
