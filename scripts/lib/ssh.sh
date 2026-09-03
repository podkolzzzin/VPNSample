#!/usr/bin/env bash

# Builds a consistent SSH option array for the deployment and E2E scripts.

ssh_options_for() {
  local key=$1
  local known_hosts_file=${2:-}
  SSH_OPTIONS=(
    -i "$key"
    -o IdentityAgent=none
    -o IdentitiesOnly=yes
    -o StrictHostKeyChecking=accept-new
    -o ConnectTimeout=15
  )
  if [[ -n $known_hosts_file ]]; then
    SSH_OPTIONS+=(-o "UserKnownHostsFile=$known_hosts_file")
  fi
}

wait_for_ssh() {
  local ip=$1
  local key=$2
  local user=${3:-root}
  local attempts=${4:-30}
  local delay=${5:-10}
  local known_hosts_file=${6:-}

  ssh_options_for "$key" "$known_hosts_file"
  for attempt in $(seq 1 "$attempts"); do
    if ssh "${SSH_OPTIONS[@]}" "$user@$ip" true 2>/dev/null; then
      return
    fi
    ((attempt < attempts)) || break
    log "SSH not ready on $ip ($attempt/$attempts); retrying in $delay seconds..."
    sleep "$delay"
  done
  fail "SSH did not become ready on $ip."
}
