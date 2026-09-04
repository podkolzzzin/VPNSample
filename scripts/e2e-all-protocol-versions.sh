#!/usr/bin/env bash
set -Eeuo pipefail

script_dir=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)
project_root=${VPNSAMPLE_ROOT:-$(cd -- "$script_dir/.." && pwd)}
state_file=${VPN_STATE_FILE:-$project_root/.vpn-droplet.env}
results_dir=${E2E_RESULTS_DIR:-$script_dir/results}
mkdir -p "$results_dir"
run_id=$(date -u +%Y%m%dT%H%M%SZ)
summary=$results_dir/protocol-versions-$run_id.tsv
work_root=$(mktemp -d /tmp/vpnsample-versions.XXXXXXXX)
cleanup() { git -C "$project_root" worktree remove --force "$work_root/stage" >/dev/null 2>&1 || true; rm -rf -- "$work_root"; }
trap cleanup EXIT

printf 'stage\tcommit\tresult\tinstagram_http\tduration_seconds\tlog\n' >"$summary"
if [[ -n ${E2E_STAGES:-} ]]; then
  read -r -a stages <<<"$E2E_STAGES"
else
  mapfile -t stages < <(git -C "$project_root" tag --list 'stage-[0-9][0-9]-*' --sort=version:refname)
fi
((${#stages[@]})) || { echo 'No stage tags found.' >&2; exit 1; }

overall=0
for stage in "${stages[@]}"; do
  commit=$(git -C "$project_root" rev-parse "$stage^{commit}")
  log=$results_dir/$run_id-$stage.log
  started=$(date +%s)
  git -C "$project_root" worktree add --detach "$work_root/stage" "$commit" >/dev/null
  status=PASS
  {
    echo "stage=$stage"
    echo "commit=$commit"
    # Each historical installer replaces its private dotnet host. Stop the
    # previous version first so Linux does not reject that copy with ETXTBSY.
    source "$state_file"
    ssh -i "$SSH_KEY_PATH" -o IdentityAgent=none -o IdentitiesOnly=yes \
      -o StrictHostKeyChecking=accept-new "root@$DROPLET_IP" \
      'systemctl stop vpnsample.service 2>/dev/null || true'
    VPN_STATE_FILE="$state_file" "$work_root/stage/scripts/deploy-server.sh"
    VPNSAMPLE_ROOT="$work_root/stage" VPN_STATE_FILE="$state_file" "$script_dir/deploy-client-ruvds.sh"
    "$script_dir/e2e-ruvds.sh"
  } >"$log" 2>&1 || { status=FAIL; overall=1; }
  code=$(awk -F': ' '/^Instagram HTTP:/ {value=$2} END {print value}' "$log")
  duration=$(($(date +%s) - started))
  printf '%s\t%s\t%s\t%s\t%s\t%s\n' "$stage" "$commit" "$status" "${code:--}" "$duration" "$log" >>"$summary"
  printf '%-38s %s (%ss, Instagram %s)\n' "$stage" "$status" "$duration" "${code:--}"
  git -C "$project_root" worktree remove --force "$work_root/stage" >/dev/null
done

standard_log=$results_dir/$run_id-standard-protocols.log
if [[ ${E2E_SKIP_STANDARD:-false} == true ]]; then
  printf 'Summary: %s\nStandard protocols: skipped\n' "$summary"
else
  "$script_dir/e2e-standard-protocols.sh" | tee "$standard_log" || overall=1
  printf 'Summary: %s\nStandard protocols: %s\n' "$summary" "$standard_log"
fi
exit "$overall"
