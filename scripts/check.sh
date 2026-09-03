#!/usr/bin/env bash
set -Eeuo pipefail

SCRIPT_DIR=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)
PROJECT_ROOT=$(cd -- "$SCRIPT_DIR/.." && pwd)
scripts=(
  "$PROJECT_ROOT/e2e_vpn_iptest.sh"
  "$SCRIPT_DIR"/*.sh
  "$SCRIPT_DIR/lib"/*.sh
  "$SCRIPT_DIR/remote"/*.sh
  "$SCRIPT_DIR/tests"/*.sh
)

bash -n "${scripts[@]}"

if command -v shellcheck >/dev/null 2>&1; then
  shellcheck "${scripts[@]}"
else
  printf '[check] shellcheck is not installed; skipped static analysis.\n' >&2
fi

"$SCRIPT_DIR/tests/lib-tests.sh"

for entrypoint in create-droplet.sh deploy-server.sh run-vpn.sh e2e-three-node.sh; do
  [[ -x $SCRIPT_DIR/$entrypoint ]] || continue
  "$SCRIPT_DIR/$entrypoint" --help >/dev/null
done
[[ ! -x $PROJECT_ROOT/e2e_vpn_iptest.sh ]] \
  || "$PROJECT_ROOT/e2e_vpn_iptest.sh" --help >/dev/null

git -C "$PROJECT_ROOT" diff --check
printf '[check] Shell syntax, help paths, and patch whitespace are valid.\n'
