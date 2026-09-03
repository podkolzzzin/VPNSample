#!/usr/bin/env bash

checkout_adjacent_tag() {
  local direction=$1
  local repo_root
  local head
  local current_index=-1
  local target_index

  need git
  repo_root=$(git -C "$SCRIPT_DIR" rev-parse --show-toplevel 2>/dev/null) \
    || fail "The script must be run from a Git checkout."
  [[ -z $(git -C "$repo_root" status --porcelain) ]] \
    || fail "The worktree has uncommitted changes; commit or stash them first."

  git -C "$repo_root" fetch --force --tags origin
  mapfile -t demo_stage_tags < <(
    git -C "$repo_root" tag --list 'stage-[0-9][0-9]-*' --sort=version:refname
  )
  ((${#demo_stage_tags[@]} > 0)) || fail "No demo stage tags were found."
  head=$(git -C "$repo_root" rev-parse HEAD)
  for index in "${!demo_stage_tags[@]}"; do
    if [[ $(git -C "$repo_root" rev-parse "${demo_stage_tags[index]}^{commit}") == "$head" ]]; then
      current_index=$index
      break
    fi
  done
  ((current_index >= 0)) \
    || fail "HEAD is not exactly on a demo stage tag. Check out stage-01-basic-tunnel first."

  target_index=$((current_index + direction))
  if ((target_index < 0 || target_index >= ${#demo_stage_tags[@]})); then
    log "Already at the $([[ $direction -lt 0 ]] && printf first || printf last) demo stage."
    return
  fi

  git -C "$repo_root" switch --detach "${demo_stage_tags[target_index]}"
  log "Now at ${demo_stage_tags[target_index]}."
}
