#!/usr/bin/env bash

# Small, retry-aware wrappers around the DigitalOcean lifecycle operations.

do_wait_until_absent() {
  local resource=$1
  local id=$2
  local label=$3
  local attempts=${4:-30}

  for attempt in $(seq 1 "$attempts"); do
    if ! doctl compute "$resource" get "$id" >/dev/null 2>&1; then
      return
    fi
    log "$label deletion is still propagating ($attempt/$attempts); retrying in 2 seconds..."
    sleep 2
  done
  return 1
}

do_delete_droplet() {
  local id=$1
  local name=$2
  if ! doctl compute droplet get "$id" >/dev/null 2>&1; then
    log "Droplet $id is already absent."
    return
  fi

  log "Deleting temporary droplet $name (ID $id)..."
  doctl compute droplet delete "$id" --force || return 1
  do_wait_until_absent droplet "$id" Droplet \
    || return 1
  log "Droplet $id is deleted."
}

do_delete_ssh_key() {
  local id=$1
  if ! doctl compute ssh-key get "$id" >/dev/null 2>&1; then
    log "DigitalOcean SSH key $id is already absent."
    return
  fi

  log "Removing temporary DigitalOcean SSH key $id..."
  doctl compute ssh-key delete "$id" --force || return 1
  do_wait_until_absent ssh-key "$id" 'SSH key' \
    || return 1
}

do_wait_for_droplet_active() {
  local id=$1
  local status=
  for attempt in $(seq 1 60); do
    status=$(doctl compute droplet get "$id" --format Status --no-header)
    [[ $status == active ]] && return
    ((attempt < 60)) || break
    log "Status: ${status:-unknown} ($attempt/60); retrying in 5 seconds..."
    sleep 5
  done
  fail "Droplet did not become active within five minutes."
}

do_wait_for_ssh_key() {
  local id=$1
  for attempt in $(seq 1 15); do
    doctl compute ssh-key get "$id" >/dev/null 2>&1 && return
    ((attempt < 15)) || break
    sleep 2
  done
  fail "DigitalOcean SSH key $id did not become visible in the API."
}
