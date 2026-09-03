#!/usr/bin/env bash
set -Eeuo pipefail

SCRIPT_DIR=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)
VPNSAMPLE_LOG_PREFIX=checkout-prev-tag
# shellcheck source=lib/common.sh
source "$SCRIPT_DIR/lib/common.sh"
# shellcheck source=lib/stage-navigation.sh
source "$SCRIPT_DIR/lib/stage-navigation.sh"

[[ $# == 0 ]] || fail "Usage: $0"
checkout_adjacent_tag -1
