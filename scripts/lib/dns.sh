#!/usr/bin/env bash

# Configures systemd-resolved to send the private VPN zone to the overlay DNS.

vpn_dns_apply() {
  local interface=$1
  local dns_server=$2
  local zone=$3

  resolvectl dns "$interface" "$dns_server"
  resolvectl domain "$interface" "$zone"
  resolvectl default-route "$interface" false
  resolvectl flush-caches
}

vpn_dns_revert() {
  local interface=$1
  resolvectl revert "$interface" 2>/dev/null || true
  resolvectl flush-caches 2>/dev/null || true
}
